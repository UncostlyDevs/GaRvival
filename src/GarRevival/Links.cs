using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace GarRevival
{
    // A two-way message pipe to one other player. Session code only sees
    // whole messages; how they travel (direct TCP, Steam) is the link's job.
    // Everything is polled from the game's main thread.
    internal abstract class Link
    {
        public string Label = string.Empty;
        public bool Closed;

        public abstract void Send(byte[] message);

        // The next complete message, or null when none is waiting.
        public abstract byte[] Receive();

        public abstract void Close();
    }

    // Direct connection. Each message is a 4-byte length followed by the bytes.
    internal sealed class TcpLink : Link
    {
        private const int MaxMessage = 1 << 20;
        private readonly TcpClient _client;
        private readonly Queue<byte[]> _unsent = new Queue<byte[]>();
        private readonly byte[] _chunk = new byte[16384];
        private IAsyncResult _connecting;
        private NetworkStream _stream;
        private byte[] _received = new byte[0];

        public TcpLink(TcpClient accepted)
        {
            _client = accepted;
            _client.NoDelay = true;
            _stream = accepted.GetStream();
            Label = accepted.Client.RemoteEndPoint.ToString();
        }

        public TcpLink(string host, int port)
        {
            Label = host + ":" + port;
            _client = new TcpClient();
            _client.NoDelay = true;
            _connecting = _client.BeginConnect(host, port, null, null);
        }

        public bool Connecting
        {
            get { return _connecting != null; }
        }

        public override void Send(byte[] message)
        {
            if (Closed)
            {
                return;
            }
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
            if (Closed)
            {
                return null;
            }
            try
            {
                if (_connecting != null)
                {
                    if (!_connecting.IsCompleted)
                    {
                        return null;
                    }
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
                    if (read <= 0)
                    {
                        break;
                    }
                    byte[] grown = new byte[_received.Length + read];
                    Buffer.BlockCopy(_received, 0, grown, 0, _received.Length);
                    Buffer.BlockCopy(_chunk, 0, grown, _received.Length, read);
                    _received = grown;
                }
                if (_received.Length >= 4)
                {
                    int length = BitConverter.ToInt32(_received, 0);
                    if (length < 0 || length > MaxMessage)
                    {
                        Fail("bad message length " + length, null);
                        return null;
                    }
                    if (_received.Length >= 4 + length)
                    {
                        byte[] message = new byte[length];
                        Buffer.BlockCopy(_received, 4, message, 0, length);
                        byte[] rest = new byte[_received.Length - 4 - length];
                        Buffer.BlockCopy(_received, 4 + length, rest, 0, rest.Length);
                        _received = rest;
                        return message;
                    }
                }
                // Readable with nothing to read means the other side hung up.
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
            RevivalLog.Info("Link " + Label + ": " + what + (ex == null ? string.Empty : " (" + ex.Message + ")"));
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

    // Accepts direct connections for a hosted session.
    internal sealed class TcpHostListener
    {
        private readonly TcpListener _listener;

        public TcpHostListener(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }

        public TcpLink Accept()
        {
            return _listener.Pending() ? new TcpLink(_listener.AcceptTcpClient()) : null;
        }

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
}
