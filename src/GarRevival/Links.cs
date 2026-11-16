using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace GarRevival;

// Two-way pipe of whole messages to one other player (TCP or Steam), polled from the main thread.
internal abstract class Link
{
    public string Label = string.Empty;
    public bool Closed;

    public abstract void Send(byte[] message);

    // Null when no complete message is waiting.
    public abstract byte[] Receive();

    public abstract void Close();
}

// Direct TCP; each message is a 4-byte length, then the bytes.
internal sealed class TcpLink : Link
{
    private const int MaxMessage = 1 << 20;
    private readonly TcpClient _client;
    private readonly Queue<byte[]> _unsent = new();
    private readonly byte[] _chunk = new byte[16384];
    private IAsyncResult _connecting;
    private NetworkStream _stream;
    private byte[] _received = [];

    public TcpLink(TcpClient accepted)
    {
        _client = accepted;
        _client.NoDelay = true;
        _stream = accepted.GetStream();
        Label = accepted.Client.RemoteEndPoint.ToString();
    }

    public TcpLink(string host, int port)
    {
        Label = $"{host}:{port}";
        _client = new TcpClient { NoDelay = true };
        _connecting = _client.BeginConnect(host, port, null, null);
    }

    public bool Connecting => _connecting != null;

    public override void Send(byte[] message)
    {
        if (Closed) return;
        if (_stream == null)
        {
            _unsent.Enqueue(message);
            return;
        }
        try
        {
            _stream.Write(BitConverter.GetBytes(message.Length), 0, 4);
            _stream.Write(message, 0, message.Length);
        }
        catch (Exception ex)
        {
            Fail("send failed", ex);
        }
    }

    public override byte[] Receive()
    {
        if (Closed) return null;
        try
        {
            if (_connecting != null)
            {
                if (!_connecting.IsCompleted) return null;
                _client.EndConnect(_connecting);
                _connecting = null;
                _stream = _client.GetStream();
                while (_unsent.Count > 0)
                {
                    Send(_unsent.Dequeue());
                }
            }
            while (_stream.DataAvailable)
            {
                int read = _stream.Read(_chunk, 0, _chunk.Length);
                if (read <= 0) break;
                var grown = new byte[_received.Length + read];
                Buffer.BlockCopy(_received, 0, grown, 0, _received.Length);
                Buffer.BlockCopy(_chunk, 0, grown, _received.Length, read);
                _received = grown;
            }
            if (_received.Length >= 4)
            {
                int length = BitConverter.ToInt32(_received, 0);
                if (length < 0 || length > MaxMessage)
                {
                    Fail($"bad message length {length}", null);
                    return null;
                }
                if (_received.Length >= 4 + length)
                {
                    var message = new byte[length];
                    Buffer.BlockCopy(_received, 4, message, 0, length);
                    var rest = new byte[_received.Length - 4 - length];
                    Buffer.BlockCopy(_received, 4 + length, rest, 0, rest.Length);
                    _received = rest;
                    return message;
                }
            }
            // Readable with nothing to read: the other side hung up.
            if (_client.Client.Poll(0, SelectMode.SelectRead) && _client.Client.Available == 0)
            {
                Closed = true;
            }
        }
        catch (Exception ex)
        {
            Fail("connection failed", ex);
        }
        return null;
    }

    private void Fail(string what, Exception ex)
    {
        RevivalLog.Info($"Link {Label}: {what}" + (ex == null ? string.Empty : $" ({ex.Message})"));
        Close();
    }

    public override void Close()
    {
        Closed = true;
        try
        {
            _client.Close();
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class TcpHostListener
{
    private readonly TcpListener _listener;

    public TcpHostListener(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public TcpLink Accept() => _listener.Pending() ? new TcpLink(_listener.AcceptTcpClient()) : null;

    public void Stop()
    {
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
        }
    }
}
