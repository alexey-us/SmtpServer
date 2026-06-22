using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SmtpServer.Protocol;
using SmtpServer.Text;

namespace SmtpServer.IO
{
    internal static class PipeReaderExtensions
    {
        // ReSharper disable once InconsistentNaming
        static readonly byte[] CRLF = { 13, 10 };
        static readonly byte[] DotBlock = { 13, 10, 46, 13, 10 };
        static readonly byte[] DotBlockStuffing = { 13, 10, 46, 46 };

        /// <summary>
        /// Read from the reader until the sequence is found.
        /// </summary>
        /// <param name="reader">The reader to read from.</param>
        /// <param name="sequence">The sequence to find to terminate the read operation.</param>
        /// <param name="func">The callback to execute to process the buffer.</param>
        /// <param name="maxMessageSizeOptions">Handling of MaxMessageSize.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The value that was read from the buffer.</returns>
        static async ValueTask ReadUntilAsync(PipeReader reader, byte[] sequence, Func<ReadOnlySequence<byte>, Task> func, IMaxMessageSizeOptions maxMessageSizeOptions, CancellationToken cancellationToken)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var read = await reader.ReadAsync(cancellationToken);
            var head = read.Buffer.Start;

            if (IsInvalidUtf8Sequence(read.Buffer))
            {
                throw new Exceptions.PipeInvalidReadFallbackException();
            }

            while (read.IsCanceled == false && read.IsCompleted == false && read.Buffer.IsEmpty == false)
            {
                if (maxMessageSizeOptions.Handling == MaxMessageSizeHandling.Strict && read.Buffer.Length > maxMessageSizeOptions.Length)
                {
                    throw new SmtpResponseException(SmtpResponse.MaxMessageSizeExceeded, true);
                }

                if (read.Buffer.TryFind(sequence, ref head, out var tail))
                {
                    try
                    {
                        await func(read.Buffer.Slice(read.Buffer.Start, head));
                    }
                    finally
                    {
                        reader.AdvanceTo(tail);
                    }

                    return;
                }

                reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);

                read = await reader.ReadAsync(cancellationToken);
            }
        }

        static bool IsInvalidUtf8Sequence(ReadOnlySequence<byte> sequence)
        {
            // Настраиваем кодировку на выброс исключения при бинарном мусоре
            var utf8ThrowOnError = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

            // Получаем декодер, который умеет сохранять состояние между сегментами
            Decoder decoder = utf8ThrowOnError.GetDecoder();

            // Временный буфер на стеке для приема символов (память в куче не выделяется)
            Span<char> charBuffer = stackalloc char[256];

            try
            {
                foreach (ReadOnlyMemory<byte> segment in sequence)
                {
                    ReadOnlySpan<byte> span = segment.Span;

                    int bytesUsed = 0;
                    int charsUsed = 0;
                    bool completed = false;

                    // Обрабатываем текущий сегмент байт
                    while (!completed)
                    {
                        decoder.Convert(
                            bytes: span.Slice(bytesUsed),
                            chars: charBuffer,
                            flush: false, // false, так как поток данных еще продолжается
                            out int bytesConsumed,
                            out int charsWritten,
                            out completed);

                        bytesUsed += bytesConsumed;
                        charsUsed += charsWritten;
                    }
                }

                // Финальный сброс (flush) декодера для проверки зависших хвостов в конце последовательности
                decoder.Convert(
                    bytes: ReadOnlySpan<byte>.Empty,
                    chars: charBuffer,
                    flush: true,
                    out _, out _, out _);

                return false; // Текст валиден
            }
            catch (DecoderFallbackException)
            {
                return true; // Обнаружен бинарный мусор
            }
        }

        /// <summary>
        /// Reads a line from the reader.
        /// </summary>
        /// <param name="reader">The reader to read from.</param>
        /// <param name="func">The action to process the buffer.</param>
        /// <param name="maxMessageSizeOptions">Handling of MaxMessageSize.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that can be used to wait on the operation on complete.</returns>
        internal static ValueTask ReadLineAsync(this PipeReader reader, Func<ReadOnlySequence<byte>, Task> func, IMaxMessageSizeOptions maxMessageSizeOptions, CancellationToken cancellationToken = default)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            return ReadUntilAsync(reader, CRLF, func, maxMessageSizeOptions, cancellationToken);
        }

        /// <summary>
        /// Reads a line from the reader.
        /// </summary>
        /// <param name="reader">The reader to read from.</param>
        /// <param name="maxMessageSizeOptions">Handling of MaxMessageSize.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that can be used to wait on the operation on complete.</returns>
        internal static ValueTask<string> ReadLineAsync(this PipeReader reader, IMaxMessageSizeOptions maxMessageSizeOptions, CancellationToken cancellationToken = default)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            return reader.ReadLineAsync(Encoding.ASCII, maxMessageSizeOptions, cancellationToken);
        }

        /// <summary>
        /// Reads a line from the reader.
        /// </summary>
        /// <param name="reader">The reader to read from.</param>
        /// <param name="encoding">The encoding to use when converting the input.</param>
        /// <param name="maxMessageSizeOptions"> Handling of MaxMessageSize</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that can be used to wait on the operation on complete.</returns>
        internal static async ValueTask<string> ReadLineAsync(this PipeReader reader, Encoding encoding, IMaxMessageSizeOptions maxMessageSizeOptions, CancellationToken cancellationToken = default)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var text = string.Empty;

            await reader.ReadLineAsync(
                buffer =>
                {
                    text = StringUtil.Create(buffer, encoding);

                    return Task.CompletedTask;
                },
                maxMessageSizeOptions,
                cancellationToken);

            return text;
        }

        /// <summary>
        /// Reads a line from the reader.
        /// </summary>
        /// <param name="reader">The reader to read from.</param>
        /// <param name="func">The action to process the buffer.</param>
        /// <param name="maxMessageSizeOptions">Handling of MaxMessageSize.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The value that was read from the buffer.</returns>
        internal static async ValueTask ReadDotBlockAsync(this PipeReader reader, Func<ReadOnlySequence<byte>, Task> func, IMaxMessageSizeOptions maxMessageSizeOptions, CancellationToken cancellationToken = default)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            await ReadUntilAsync(
                reader,
                DotBlock,
                buffer =>
                {
                    buffer = Unstuff(buffer);

                    return func(buffer);
                },
                maxMessageSizeOptions,
                cancellationToken);

            static ReadOnlySequence<byte> Unstuff(ReadOnlySequence<byte> buffer)
            {
                var head = buffer.GetPosition(0);
                var start = head;

                var segments = new ByteArraySegmentList();

                while (buffer.TryFind(DotBlockStuffing, ref head, out var tail))
                {
                    var slice = buffer.Slice(start, buffer.GetPosition(3, head));

                    segments.Append(ref slice);

                    start = tail;
                    head = tail;
                }

                var remaining = buffer.Slice(start);
                segments.Append(ref remaining);

                return segments.Build();
            }
        }
    }
}
