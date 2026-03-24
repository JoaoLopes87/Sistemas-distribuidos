using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

class Server
{
    static void Main()
    {
        TcpListener server = new TcpListener(IPAddress.Any, 6000);
        server.Start();

        Console.WriteLine("Server started on port 6000");

        while (true)
        {
            TcpClient client = server.AcceptTcpClient();
            Console.WriteLine("Gateway connected");

            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[1024];

            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                Console.WriteLine("Received: " + message);

                File.AppendAllText("data.csv", message + "\n");
            }

            client.Close();
        }
    }
}