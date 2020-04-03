using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;

namespace file_transfer
{

    public delegate void TransferEventHandler(object sender, TransferQueue queue);
    public delegate void ConnectCallBack(object sender, string error);

    public class TransferClient
    {
        private Socket _baseSocket;

        private byte[] _buffer = new byte[8192];

        private ConnectCallBack _connectCallBack;

        private Dictionary<int, TransferQueue> _transfers = new Dictionary<int, TransferQueue>();

        public Dictionary<int, TransferQueue> Transfers
        {
            get { return _transfers; }
        }

        public bool Closed
        {
            get;
            private set;
        }

        public string OutputFolder
        {
            get;
            set;
        }

        public IPEndPoint EndPoint
        {
            get;
            private set;
        }

        public event TransferEventHandler Queued;
        public event TransferEventHandler ProgressChanged;
        public event TransferEventHandler Stoped;
        public event TransferEventHandler Complete;
        public event EventHandler Disconnected;

        public TransferClient()
        {
            _baseSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public TransferClient(Socket sock)
        {
            _baseSocket = sock;
            EndPoint = (IPEndPoint)_baseSocket.RemoteEndPoint;

        }

        public void Connect(string hostName, int port, ConnectCallBack callBack)
        {
            _connectCallBack = callBack;

            _baseSocket.BeginConnect(hostName, port, connectCallBack, null);

        }

        public void connectCallBack(IAsyncResult ar)
        {
            string error = null;
            try
            {
                _baseSocket.EndConnect(ar);
                EndPoint = (IPEndPoint)_baseSocket.RemoteEndPoint;


            }
            catch (Exception ex)
            {

                error = ex.Message;
            }
            _connectCallBack(this, error);
        }

        public void Run()
        {
            try
            {
                _baseSocket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.Peek, receiveCallBack, null);


            }
            catch
            {

                Close();
            }

        }

        public void QueueTransfer(string fileName)
        {
            try
            {
                TransferQueue queue = TransferQueue.CreateUploadQueue(this, fileName);
                _transfers.Add(queue.ID, queue);

                PacketWriter pw = new PacketWriter();
                pw.Write((byte)Headers.Queue);
                pw.Write(queue.ID);
                pw.Write(queue.FileName);
                pw.Write(queue.Length);
                Send(pw.GetBytes());

                if (Queued != null)
                {
                    Queued(this, queue);
                }

            }
            catch { }
        }

        public void startTransfer(TransferQueue queue)
        {
            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Start);
            pw.Write(queue.ID);
            Send(pw.GetBytes());

        }

        public void stopTransfer(TransferQueue queue)
        {
            if (queue.Type == QueueType.Upload)
            {
                queue.Stop();
            }
            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Stop);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
            queue.Close();
        }

        public void pauseTransfer(TransferQueue queue)
        {
            if (queue.Type == QueueType.Upload)
            {
                queue.Pause();
                return;
            }
            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Pause);
            pw.Write(queue.ID);
            Send(pw.GetBytes());

        }

        public int GetOverallProgress()
        {
            int overall = 0;
            foreach (KeyValuePair<int, TransferQueue> pair in _transfers)
            {
                overall += pair.Value.Progress;
            }
            if (overall > 0)
            {
                overall = (overall * 100) / (_transfers.Count * 100);

            }
            return overall;

        }

        public void Send(byte[] data)
        {
            if (Closed)
                return;

            lock (this)
            {
                try
                {
                    _baseSocket.Send(BitConverter.GetBytes(data.Length), 0, 4, SocketFlags.None);
                    _baseSocket.Send(data, 0, data.Length, SocketFlags.None);

                }
                catch
                {

                    Close();
                }

            }
        }

        public void Close()
        {
            Closed = true;
            _baseSocket.Close();
            _transfers.Clear();
            _transfers = null;
            _buffer = null;
            OutputFolder = null;

            if (Disconnected != null)

                Disconnected(this, EventArgs.Empty);

        }

        private void process()
        {
            PacketReader pr = new PacketReader(_buffer);

            Headers header = (Headers)pr.ReadByte();

            switch (header)
            {
                case Headers.Queue:
                    {
                        int id = pr.ReadInt32();
                        string fileName = pr.ReadString();
                        long length = pr.ReadInt64();

                        TransferQueue queue = TransferQueue.CreateDownloadQueue(this, id, Path.Combine(OutputFolder, Path.GetFileName(fileName)), length);

                        _transfers.Add(id, queue);

                        if (Queued != null)
                        {
                            Queued(this, queue);
                        }
                    }
                    break;
                case Headers.Start:
                    {
                        int id = pr.ReadInt32();
                        if (_transfers.ContainsKey(id))
                        {
                            _transfers[id].Start();
                        }
                    }
                    break;
                case Headers.Stop:
                    {
                        int id = pr.ReadInt32();
                        if (_transfers.ContainsKey(id))
                        {
                            TransferQueue queue = _transfers[id];
                            queue.Stop();
                            queue.Close();

                            if (Stoped != null)
                                Stoped(this, queue);
                            _transfers.Remove(id);
                        }
                    }
                    break;
                case Headers.Pause:
                    {
                        int id = pr.ReadInt32();
                        if (_transfers.ContainsKey(id))
                        {
                            _transfers[id].Pause();
                        }
                    }
                    break;
                case Headers.Chunk:
                    {
                        int id = pr.ReadInt32();
                        long index = pr.ReadInt64();
                        int size = pr.ReadInt32();
                        byte[] buffer = pr.ReadBytes(size);

                        TransferQueue queue = _transfers[id];
                        queue.Write(buffer, index);
                        queue.Progress = (int)((queue.Transfered * 100) / queue.Length);

                        if (queue.LastProgress < queue.Progress)
                        {
                            queue.LastProgress = queue.Progress;
                            if (ProgressChanged != null)
                            {
                                ProgressChanged(this, queue);
                            }
                            if (queue.Progress == 100)
                            {
                                queue.Close();
                                if (Complete != null)
                                {
                                    Complete(this, queue);
                                }
                            }
                        }

                    }
                    break;
            }
            pr.Dispose();
        }

        private void receiveCallBack(IAsyncResult ar)
        {
            try
            {
                int found = _baseSocket.EndReceive(ar);
                if (found >= 4)
                {
                    _baseSocket.Receive(_buffer, 0, 4, SocketFlags.None);

                    int size = BitConverter.ToInt32(_buffer, 0);
                    int read = _baseSocket.Receive(_buffer, 0, size, SocketFlags.None);

                    while (read < size)
                    {
                        read += _baseSocket.Receive(_buffer, read, size - read, SocketFlags.None);

                    }
                    process();
                }

                Run();
            }
            catch (Exception)
            {

                Close();
            }

        }

        internal void CallProgressChanged(TransferQueue queue)
        {
            if (ProgressChanged != null)
            {
                ProgressChanged(this, queue);
            }
        }
    }
}
