using Crestron.SimplSharp;
using Microsoft.Owin;
using Owin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

[assembly: OwinStartup(typeof(Eylar.OwinStartup))]

namespace Eylar
{
    class OwinStartup
    {
        public void Configuration(IAppBuilder app)
        {
            CrestronConsole.PrintLine("OwinStartup.Configuration");
            app.UseErrorPage();
            
            app.Run(context =>
            {
                try
                {
                    CrestronConsole.PrintLine($"OwinStartup.Run: {context.Request.Path}");
                    context.Response.ContentType = "text/plain";
                    return context.Response.WriteAsync("Hello from OWIN");
                }
                catch (Exception e)
                {
                    CrestronConsole.PrintLine($"OwinStartup.Run: {e}");
                    return null;
                }
            });
        }
    }
}
