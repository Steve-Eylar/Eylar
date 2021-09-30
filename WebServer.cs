using Crestron.SimplSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Eylar
{
    class WebServer
    {
        HttpListener Listener;

        public WebServer(int port)
        {
            Listener = new HttpListener { Prefixes = { $"http://localhost:{port}/" } };
            Listener.Start();
            while (true)
            {
                var status = Listener.IsListening ? "" : "not ";
                CrestronConsole.PrintLine($"WebServer: is {status}listening");
                var context = Listener.GetContext();
                CrestronConsole.PrintLine($"WebServer: {context.Request.QueryString}");
                context.Response.ContentType = "text/plain";
                var buffer = Encoding.UTF8.GetBytes("Hello from WebServer");
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            }
        }
    }
}
