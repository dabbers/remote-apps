// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Threading.Channels;

namespace RemoteApp.Protocol;

/// <summary>
/// Wraps a byte stream (normally an <see cref="System.Net.Security.SslStream"/>)
/// with message framing: a receive loop that decodes frames and raises
/// <see cref="ControlReceived"/> / <see cref="VideoReceived"/>, and a single
/// writer that serialises sends. Safe to call <c>Send</c> from any thread;
/// callbacks arrive on a thread-pool thread, one at a time, in order.
/// </summary>
public sealed class MessageConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly Channel<(byte[] Frame, Action<Exception?>? Completion)> _outbox =
        Channel.CreateUnbounded<(byte[], Action<Exception?>?)>();   // the default reader supports Count; the single-reader one does not
    private readonly CancellationTokenSource _cts = new();
    private int _closed;
    private int _queuedVideoFrames;

    public event Action<ControlMessage>? ControlReceived;
    public event Action<VideoFrame>? VideoReceived;
    /// <summary>Raised once, with the error that ended the connection (null for a clean close).</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Video frames queued but not yet handed to the socket; the server drops frames when this is high.</summary>
    public int QueuedVideoFrames => Volatile.Read(ref _queuedVideoFrames);

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public MessageConnection(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Starts the receive and send loops.</summary>
    public void Start()
    {
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(SendLoopAsync);
    }

    public void Send(ControlMessage message, Action<Exception?>? completion = null)
    {
        var frame = MessageCodec.Encode(message);
        if (!_outbox.Writer.TryWrite((frame, completion))) completion?.Invoke(new ObjectDisposedException(nameof(MessageConnection)));
    }

    public void Send(VideoFrame video, Action<Exception?>? completion = null)
    {
        var frame = MessageCodec.Encode(video);
        Interlocked.Increment(ref _queuedVideoFrames);
        var wrapped = new Action<Exception?>(e =>
        {
            Interlocked.Decrement(ref _queuedVideoFrames);
            completion?.Invoke(e);
        });
        if (!_outbox.Writer.TryWrite((frame, wrapped))) wrapped(new ObjectDisposedException(nameof(MessageConnection)));
    }

    /// <summary>Closes the connection; <see cref="Closed"/> fires with null.</summary>
    public void Close() => Finish(null);

    public void Dispose() => Finish(null);

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length < 1 || length > MessageCodec.MaxFrameLength)
                    throw new FramingException($"frame length {length} out of range");
                var body = new byte[length];
                await _stream.ReadExactlyAsync(body, _cts.Token).ConfigureAwait(false);
                var message = MessageCodec.DecodePayload(body[0], body.AsMemory(1));
                switch (message)
                {
                    case ControlFrame c: ControlReceived?.Invoke(c.Control); break;
                    case VideoFrameMessage v: VideoReceived?.Invoke(v.Frame); break;
                }
            }
        }
        catch (OperationCanceledException) { Finish(null); }
        catch (EndOfStreamException) { Finish(null); }
        catch (Exception e) { Finish(e); }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out var item))
                {
                    try
                    {
                        await _stream.WriteAsync(item.Frame, _cts.Token).ConfigureAwait(false);
                        // Only flush when nothing else is queued, so bursts coalesce.
                        if (_outbox.Reader.Count == 0)
                            await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                        item.Completion?.Invoke(null);
                    }
                    catch (Exception e)
                    {
                        item.Completion?.Invoke(e);
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) { Finish(null); }
        catch (Exception e) { Finish(e); }
    }

    private void Finish(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        // Fail any sends still queued so in-flight counters unwind.
        while (_outbox.Reader.TryRead(out var item))
            item.Completion?.Invoke(error ?? new ObjectDisposedException(nameof(MessageConnection)));
        try { _stream.Dispose(); } catch { /* best effort */ }
        Closed?.Invoke(error);
    }
}
