using System.Collections.Generic;

class SensorInfo
{
    public string Estado = "";
    public string Zona = "";
    public string[] Tipos = [];

    public SensorInfo(string estado, string zona, string[] tipos)
    {
        Estado=estado;
        Zona=zona;
        Tipos=tipos;
    }
}