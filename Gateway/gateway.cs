using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

class Gateway
{
    static void Main()
    {
        TcpListener listener = new TcpListener(IPAddress.Any, 5000);
        listener.Start();

        Console.WriteLine("Gateway started on port 5000");

        while (true)
        {
            TcpClient sensorClient = listener.AcceptTcpClient();
            Console.WriteLine("Sensor connected");

            HandleSensor(sensorClient);
        }
    }

    static void HandleSensor(TcpClient sensorClient)
    {
        NetworkStream sensorStream = sensorClient.GetStream();
        byte[] buffer = new byte[1024];
        int bytesRead;

        while ((bytesRead = sensorStream.Read(buffer, 0, buffer.Length)) != 0)
        {
            string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.WriteLine("Sensor sent: " + message);

            SendToServer(message);
        }

        sensorClient.Close();
    }

    static void SendToServer(string message)
    {
        TcpClient serverClient = new TcpClient("127.0.0.1", 6000);

        NetworkStream serverStream = serverClient.GetStream();

        byte[] data = Encoding.UTF8.GetBytes(message);

        serverStream.Write(data, 0, data.Length);

        serverClient.Close();
    }
}