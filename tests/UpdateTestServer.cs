using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ClearDesk
{
    // Tiny static-file HTTP/1.1 server used by the update tests (Range support, optional mid-download cut).
    // Also reachable as:  ClearDesk.exe --update-test-server <folder> <port> [cutBytes]
    internal sealed class UpdateTestServer : IDisposable
    {
        readonly TcpListener listener; readonly string root; volatile bool stop; Thread thread;
        public int Port { get; private set; }
        public long CutFirstBytes;                       // first full (non-Range) request of a .zip is cut after this many bytes
        public int RangeRequests, FullRequests, Requests;
        public List<string> Log = new List<string>();
        public string LogFile;
        bool cutDone;

        public UpdateTestServer(string folder, int port = 0)
        {
            root = Path.GetFullPath(folder);
            listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Loop) { IsBackground = true }; thread.Start();
        }
        public string Url(string path) { return "http://127.0.0.1:" + Port + "/" + path.TrimStart('/'); }

        void Loop()
        {
            while (!stop)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { break; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch { } finally { try { c.Close(); } catch { } } });
            }
        }

        void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 5000; var s = c.GetStream();
            var sb = new StringBuilder(); var one = new byte[1];
            while (sb.Length < 16384 && s.Read(one, 0, 1) == 1) { sb.Append((char)one[0]); if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break; }
            var lines = sb.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var parts = lines[0].Split(' '); if (parts.Length < 2) return;
            string method = parts[0], path = Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
            string range = lines.Skip(1).Select(l => l.Split(new[] { ':' }, 2)).Where(p => p.Length == 2 && p[0].Equals("Range", StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();
            lock (Log) { Requests++; string line = method + " " + path + (range != null ? " [" + range + "]" : ""); Log.Add(line); if (LogFile != null) { try { File.AppendAllText(LogFile, line + "\n"); } catch { } } }
            string full = Path.GetFullPath(Path.Combine(root, path.Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { Send(s, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"), null); return; }
            var data = File.ReadAllBytes(full);
            string type = full.EndsWith(".json") ? "application/json" : full.EndsWith(".zip") ? "application/zip" : "application/octet-stream";
            long start = 0, end = data.Length - 1; bool partial = false;
            if (range != null && range.StartsWith("bytes="))
            {
                var r = range.Substring(6).Split('-'); long a; if (long.TryParse(r[0], out a)) { start = a; partial = true; if (r.Length > 1 && r[1].Length > 0) end = Math.Min(end, long.Parse(r[1])); }
                if (start >= data.Length) { Send(s, "416 Range Not Satisfiable", type, new byte[0], null); return; }
            }
            lock (Log) { if (partial) RangeRequests++; else FullRequests++; }
            long len = end - start + 1; var head = new StringBuilder();
            head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n").Append("Content-Type: " + type + "\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + len + "\r\n");
            if (partial) head.Append("Content-Range: bytes " + start + "-" + end + "/" + data.Length + "\r\n");
            head.Append("\r\n");
            var hb = Encoding.ASCII.GetBytes(head.ToString()); s.Write(hb, 0, hb.Length);
            if (method == "HEAD") return;
            long send = len; bool cut = false;
            lock (Log) { if (!partial && CutFirstBytes > 0 && !cutDone && path.EndsWith(".zip")) { cutDone = true; send = Math.Min(len, CutFirstBytes); cut = true; } }
            s.Write(data, (int)start, (int)send); s.Flush();
            if (cut) { c.Client.LingerState = new LingerOption(true, 0); }     // abrupt close: client sees a truncated body
        }

        static void Send(NetworkStream s, string status, string type, byte[] body, string extra)
        {
            var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            s.Write(h, 0, h.Length); s.Write(body, 0, body.Length);
        }

        public void Dispose() { stop = true; try { listener.Stop(); } catch { } }

        internal static void RunBlocking(string[] args)
        {
            int port = int.Parse(args[2]); var srv = new UpdateTestServer(args[1], port);
            if (args.Length > 3) srv.CutFirstBytes = long.Parse(args[3]);
            srv.LogFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]).TrimEnd('\\')), "server-log-" + srv.Port + ".txt");
            File.WriteAllText(Path.Combine(args[1], "server-ready.txt"), srv.Port.ToString());
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
