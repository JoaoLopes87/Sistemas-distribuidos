public class SensorInfo
{
    public string Estado { get; set; }
    public string Zona { get; set; }
    public string[] Tipos { get; set; }
    public string LastSync { get; set; }

    public SensorInfo(string estado, string zona, string[] tipos, string lastSync)
    {
        Estado = estado;
        Zona = zona;
        Tipos = tipos;
        LastSync = lastSync;
    }
}
