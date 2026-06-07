class SensorInfo
{
    public string Estado = "";
    public string Zona = "";
    public string[] Tipos = [];
    public string LastSync = "";

    public SensorInfo(string estado, string zona, string[] tipos, string lastSync)
    {
        Estado = estado;
        Zona = zona;
        Tipos = tipos;
        LastSync = lastSync;
    }
}
