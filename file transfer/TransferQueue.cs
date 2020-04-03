using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace file_transfer
{
    public enum QueueType : byte
    {
        Download,
        Upload
    }

    public class TransferQueue
    {



        public static TransferQueue CreateUploadQueue(TransferClient client, string fileNme)
        {
            try
            {
                var queue = new TransferQueue();
                queue.FileName = Path.GetFileName(fileNme);
                queue.Client = client;
                queue.Type = QueueType.Upload;
                queue.FS = new FileStream(fileNme, FileMode.Open);
                queue.Thread = new Thread(new ParameterizedThreadStart(transferProc));
                queue.Thread.IsBackground = true;
                queue.ID = Program.Rand.Next();
                queue.Length = queue.FS.Length;

                return queue;

            }
            catch
            {

                return null;
            }

        }

        public static TransferQueue CreateDownloadQueue(TransferClient client, int id, string saveName, long length)
        {
            try
            {
                var queue = new TransferQueue();
                queue.FileName = Path.GetFileName(saveName);
                queue.Client = client;
                queue.Type = QueueType.Download;
                queue.FS = new FileStream(saveName, FileMode.Create);
                queue.FS.SetLength(length);
                queue.Length = length;
                queue.ID = id;
                return queue;

            }
            catch
            {

                return null;
            }
        }



        private const int FILE_BUFFER_SIZE = 8175;
        private static byte[] file_buffer = new byte[FILE_BUFFER_SIZE];
        private ManualResetEvent pauseEvent;

        public int ID;
        public int Progress, LastProgress;

        public long Transfered;
        public long Index;
        public long Length;

        public bool Running;
        public bool Paused;

        public string FileName;

        public QueueType Type;

        public TransferClient Client;
        public Thread Thread;

        public FileStream FS;

        private TransferQueue()
        {
            pauseEvent = new ManualResetEvent(true);
            //pauseEvent.WaitOne();
            Running = true;
        }
        public void Start()
        {
            Running = true;
            Thread.Start(this);
        }
        public void Stop()
        {
            Running = false;
        }

        public void Pause()
        {
            if (!Paused)
            {
                pauseEvent.Reset();
            }
            else
            {
                pauseEvent.Set();
            }
            Paused = !Paused;
        }

        public void Close()
        {
            try
            {
                Client.Transfers.Remove(ID);
            }
            catch
            {
                Close();
            }
            Running = false;
            FS.Close();
            pauseEvent.Dispose();

            Client = null;

        }

        public void Write(byte[] bytes, long index)
        {
            lock (this)
            {
                FS.Position = index;
                FS.Write(bytes, 0, bytes.Length);
                Transfered += bytes.Length;
            }
        }

        private static void transferProc(object o)
        {
            TransferQueue queue = (TransferQueue)o;

            while (queue.Running && queue.Index < queue.Length)
            {
                queue.pauseEvent.WaitOne();

                if (!queue.Running)
                {
                    break;
                }

                lock (file_buffer)
                {
                    queue.FS.Position = queue.Index;
                    int read = queue.FS.Read(file_buffer, 0, file_buffer.Length);

                    PacketWriter pw = new PacketWriter();
                    pw.Write((byte)Headers.Chunk);
                    pw.Write(queue.ID);
                    pw.Write(queue.Index);
                    pw.Write(read);
                    pw.Write(file_buffer, 0, read);

                    queue.Transfered += read;
                    queue.Index += read;

                    queue.Client.Send(pw.GetBytes());

                    queue.Progress = (int)((queue.Transfered * 100) / queue.Length);

                    if (queue.LastProgress < queue.Progress)
                    {
                        queue.LastProgress = queue.Progress;
                        queue.Client.CallProgressChanged(queue);

                    }
                    Thread.Sleep(1);

                }

            }

            queue.Close();

        }

    }
}
