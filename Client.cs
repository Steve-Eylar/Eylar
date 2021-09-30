using Crestron.SimplSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Eylar
{
    class Client
    {
        HttpClient HttpClient;
        string Name;
        public Client(string name)
        {
            HttpClient = new HttpClient();
            Name = name;
        }

        public string Get(string request) { 

            var response = HttpClient.GetAsync(request).Result;
            var result = response.Content.ReadAsStringAsync().Result;

            CrestronConsole.PrintLine($"{Name}: {response}");
            CrestronConsole.PrintLine($"{Name}: {result}\r\n\r\n");
            return result;
        }
    }
}
