﻿using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Org.BouncyCastle.Crypto.Tls;

namespace Skateboard2Server.Host
{
    public class SslServerMiddleware
    {
        private readonly ConnectionDelegate _next;

        public SslServerMiddleware(ConnectionDelegate next)
        {
            _next = next;
        }

        public async Task OnConnectionAsync(ConnectionContext context)
        {
            var inputStream = context.Transport.Input.AsStream();
            var outputStream = context.Transport.Output.AsStream();
            var protocol = new TlsServerProtocol(inputStream, outputStream, new Org.BouncyCastle.Security.SecureRandom());
            protocol.Accept(new FeslTlsServer());
            var sslStream = protocol.Stream;


            var memoryPool = context.Features.Get<IMemoryPoolFeature>()?.MemoryPool;

            var inputPipeOptions = new StreamPipeReaderOptions
            (
                pool: memoryPool,
                bufferSize: memoryPool.GetMinimumSegmentSize(),
                minimumReadSize: memoryPool.GetMinimumAllocSize(),
                leaveOpen: true
            );


            var outputPipeOptions = new StreamPipeWriterOptions
            (
                pool: memoryPool,
                leaveOpen: true
            );

            var sslDuplexPipe = new DuplexPipeStreamAdapter<Stream>(context.Transport, inputPipeOptions, outputPipeOptions, sslStream);

            var originalTransport = context.Transport;

            try
            {
                context.Transport = sslDuplexPipe;

                // Disposing the stream will dispose the sslDuplexPipe
                await using (sslStream)
                await using (sslDuplexPipe)
                {
                    await _next(context).ConfigureAwait(false);
                    // Dispose the inner stream (SslDuplexPipe) before disposing the SslStream
                    // as the duplex pipe can hit an ODE as it still may be writing.
                }
            }
            finally
            {
                context.Transport = originalTransport;
            }
        }
    }

    internal class DuplexPipeStreamAdapter<TStream> : IAsyncDisposable, IDuplexPipe where TStream : Stream
    {
        private bool _disposed;
        private readonly object _disposeLock = new object();

        public DuplexPipeStreamAdapter(IDuplexPipe duplexPipe, StreamPipeReaderOptions readerOptions, StreamPipeWriterOptions writerOptions, TStream stream)
        {
            Stream = stream;
            Input = PipeReader.Create(stream, readerOptions);
            Output = PipeWriter.Create(stream, writerOptions);
        }

        public TStream Stream { get; }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public async ValueTask DisposeAsync()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }

            await Input.CompleteAsync().ConfigureAwait(false);
            await Output.CompleteAsync().ConfigureAwait(false);
        }
    }

    internal static class MemoryPoolExtensions
    {
        /// <summary>
        /// Computes a minimum segment size
        /// </summary>
        /// <param name="pool"></param>
        /// <returns></returns>
        public static int GetMinimumSegmentSize(this MemoryPool<byte> pool)
        {
            if (pool == null)
            {
                return 4096;
            }

            return Math.Min(4096, pool.MaxBufferSize);
        }

        public static int GetMinimumAllocSize(this MemoryPool<byte> pool)
        {
            // 1/2 of a segment
            return pool.GetMinimumSegmentSize() / 2;
        }
    }
}
