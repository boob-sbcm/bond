﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.Comm.Tcp
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Bond.IO.Safe;
    using Bond.Protocols;

    public enum ConnectionType
    {
        Client,
        Server
    }

    public class TcpConnection : Connection, IRequestResponseConnection
    {
        private TcpTransport m_parentTransport;

        TcpClient m_tcpClient;
        NetworkStream m_networkStream;

        TcpServiceHost m_serviceHost;

        object m_requestsLock;
        Dictionary<uint, TaskCompletionSource<IMessage>> m_outstandingRequests;

        long m_requestId;

        public TcpConnection(
            TcpTransport parentTransport,
            TcpClient tcpClient,
            ConnectionType connectionType)
            : this (
                  parentTransport,
                  tcpClient,
                  new TcpServiceHost(parentTransport),
                  connectionType)
        {
        }

        internal TcpConnection(
            TcpTransport parentTransport,
            TcpClient tcpClient,
            TcpServiceHost serviceHost,
            ConnectionType connectionType)
        {
            m_parentTransport = parentTransport;
            m_tcpClient = tcpClient;
            m_networkStream = tcpClient.GetStream();
            m_serviceHost = serviceHost;
            m_requestsLock = new object();
            m_outstandingRequests = new Dictionary<uint, TaskCompletionSource<IMessage>>();

            // start at -1 or 0 so the first request ID is 1 or 2.
            m_requestId = connectionType == ConnectionType.Client ? -1 : 0;
        }

        internal static Frame MessageToFrame(uint requestId, string methodName, PayloadType type, IMessage payload)
        {
            var frame = new Frame();

            {
                var tcpHeaders = new TcpHeaders
                {
                    request_id = requestId,
                    payload_type = type,
                    method_name = methodName ?? string.Empty, // method_name is not nullable
                };

                if (payload.IsError)
                {
                    tcpHeaders.error_code = payload.Error.Deserialize<Error>().error_code;
                }
                else
                {
                    tcpHeaders.error_code = (int)ErrorCode.OK;
                }

                var outputBuffer = new OutputBuffer(150);
                var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
                Serialize.To(fastWriter, tcpHeaders);

                frame.Add(new Framelet(FrameletType.TcpHeaders, outputBuffer.Data));
            }

            {
                var userData = payload.IsError ? (IBonded)payload.Error : (IBonded)payload.RawPayload;

                var outputBuffer = new OutputBuffer(1024);
                var compactWriter = new CompactBinaryWriter<OutputBuffer>(outputBuffer);
                // TODO: marshal dies on IBonded Marshal.To(compactWriter, request)
                // understand more deeply why and consider fixing
                compactWriter.WriteVersion();
                userData.Serialize(compactWriter);

                frame.Add(new Framelet(FrameletType.PayloadData, outputBuffer.Data));
            }

            return frame;
        }

        // TODO: make async for real
        internal async Task<IMessage> SendRequestAsync<TPayload>(string methodName, IMessage<TPayload> request)
        {
            uint requestId = AllocateNextRequestId();
            var frame = MessageToFrame(requestId, methodName, PayloadType.Request, request);

            var responseCompletionSource = new TaskCompletionSource<IMessage>();
            lock (m_requestsLock)
            {
                m_outstandingRequests.Add(requestId, responseCompletionSource);
            }

            try
            {
                using (var binWriter = new BinaryWriter(m_networkStream, encoding: Encoding.UTF8, leaveOpen: true))
                {
                    frame.Write(binWriter);
                    binWriter.Flush();
                }

                await m_networkStream.FlushAsync();
            }
            catch (IOException ex)
            {
                responseCompletionSource.TrySetException(ex);
            }

            return await responseCompletionSource.Task;
        }

        internal async Task SendReplyAsync(uint requestId, IMessage response)
        {
            var frame = MessageToFrame(requestId, null, PayloadType.Response, response);

            try
            {
                using (var binWriter = new BinaryWriter(m_networkStream, encoding: Encoding.UTF8, leaveOpen: true))
                {
                    frame.Write(binWriter);
                    binWriter.Flush();
                }

                await m_networkStream.FlushAsync();
            }
            catch (IOException)
            {
                // TODO: convert to an Error?
                throw;
            }
        }

        internal void Start()
        {
            Task.Run(() => ProcessFramesAsync(m_networkStream));
        }

        private UInt32 AllocateNextRequestId()
        {
            var requestIdLong = Interlocked.Add(ref m_requestId, 2);
            if (requestIdLong > UInt32.MaxValue)
            {
                throw new ProtocolErrorException("Exhausted request IDs");
            }

            return unchecked((UInt32)requestIdLong);
        }

        private async Task ProcessFramesAsync(NetworkStream stream)
        {
            // TODO: shutdown
            for (;;)
            {
                var frame = await Frame.ReadAsync(stream);

                var payload = default(ArraySegment<byte>);
                var headers = default(TcpHeaders);

                foreach(var framelet in frame.Framelets)
                {
                    switch (framelet.Type)
                    {
                        case FrameletType.TcpHeaders:
                            var inputBuffer = new InputBuffer(framelet.Contents);
                            var fastBinaryReader = new FastBinaryReader<InputBuffer>(inputBuffer, version: 1);
                            headers = Deserialize<TcpHeaders>.From(fastBinaryReader);
                            break;

                        case FrameletType.PayloadData:
                            payload = framelet.Contents;
                            break;

                        default:
                            System.Diagnostics.Debug.Write("Ignoring frame of type " + framelet.Type);
                            break;
                    }
                }

                if (headers == null)
                {
                    throw new ProtocolErrorException("Missing headers");
                }
                else if (payload.Array == null)
                {
                    throw new ProtocolErrorException("Missing payload");
                }

                switch (headers.payload_type)
                {
                    case PayloadType.Request:
                        DispatchRequest(headers, payload);
                        break;

                    case PayloadType.Response:
                        DispatchResponse(headers, payload);
                        break;

                    case PayloadType.Event:
                    default:
                        throw new NotImplementedException(headers.payload_type.ToString());
                }
            }
        }

        private void DispatchRequest(TcpHeaders headers, ArraySegment<byte> payload)
        {
            if (headers.error_code != (int)ErrorCode.OK)
            {
                throw new ProtocolErrorException("Received a request with non-zero error code. Request ID " + headers.request_id);
            }

            IMessage request = Message.FromPayload(Unmarshal.From(payload));
            m_serviceHost.DispatchRequest(headers, this, request);
        }

        private void DispatchResponse(TcpHeaders headers, ArraySegment<byte> payload)
        {
            TaskCompletionSource<IMessage> responseCompletionSource;

            lock (m_requestsLock)
            {
                if (!m_outstandingRequests.TryGetValue(headers.request_id, out responseCompletionSource))
                {
                    // TODO: this should be something we just log
                    throw new ProtocolErrorException("Response for unmatched request " + headers.request_id);
                }

                m_outstandingRequests.Remove(headers.request_id);
            }

            IMessage response;
            if (headers.error_code != (int) ErrorCode.OK)
            {
                response = Message.FromError(Unmarshal<Error>.From(payload));
            }
            else
            {
                response = Message.FromPayload(Unmarshal.From(payload));
            }

            responseCompletionSource.SetResult(response);
        }

        public override Task StopAsync()
        {
            m_tcpClient.Close();
            return TaskExt.CompletedTask;
        }

        public override void AddService<T>(T server)
        {
            // TODO: re-work when we use reflection to find service methods
            m_serviceHost.Register((IService)server);
        }

        public override void RemoveService<T>(T service)
        {
            throw new NotImplementedException();
        }

        public async Task<IMessage<TResponse>> RequestResponseAsync<TRequest, TResponse>(string methodName, IMessage<TRequest> message, CancellationToken ct)
        {
            // TODO: cancellation
            IMessage response = await SendRequestAsync(methodName, message);
            return response.Convert<TResponse>();
        }
    }
}
