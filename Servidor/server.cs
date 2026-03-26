using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

class Server
{
    static void Main()
    {
        int port=6000;
        TcpListener server= new TcpListener(IPAddress.Any, port);
        server.Start();

        Console.Write("Server started...");
        Console.Write("Wainting for connections...");

        while (true)
        {
            TcpClient client=server.AcceptTcpClient();

            Console.WriteLine("New client connected.");

            Thread clientThread=new Thread(HandleClient);

            clientThread.Start(client);
        }

    }

    static void HandleClient(object obj)
    {
        TcpClient client=(TcpClient)obj;

        NetworkStream stream=client.GetStream();

        StreamReader reader=new StreamReader(stream);

        try
        {
            while (true)
            {
                string message=reader.ReadLine();
                
                if (message == null)
                {
                    break;
                }

                Console.WriteLine("Received: " + message);
            }
        }
        catch(Exception)
        {
            Console.WriteLine("Client disconnected.");
        }

        client.Close();
    }
}