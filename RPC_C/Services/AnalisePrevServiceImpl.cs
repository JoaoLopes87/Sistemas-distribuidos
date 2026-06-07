using Grpc.Core;
using AnalisePrevGrpc;
using System.Globalization;

namespace RPC_C.Services;

public class AnalisePrevServiceImpl : AnalisePrevService.AnalisePrevServiceBase
{
    public override Task<AnalisarResponse> Analisar(AnalisarRequest request, ServerCallContext context)
    {
        if (!double.TryParse(request.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            return Task.FromResult(new AnalisarResponse
            {
                Valido = false,
                Erro   = "Value is not numeric"
            });

        (double aviso, double critico) = request.Type switch
        {
            "TEMP"  => (30.0,   40.0),
            "HUM"   => (70.0,   85.0),
            "RUIDO" => (70.0,   85.0),
            "PM2.5" => (35.0,   75.0),
            "PM10"  => (50.0,  150.0),
            "AR"    => (100.0, 200.0),
            "LUM"   => (3000.0, 10000.0),
            _       => (double.MaxValue, double.MaxValue)
        };

        string nivel, mensagem;

        if (val >= critico)
        {
            nivel    = "CRITICO";
            mensagem = $"[{request.Zona}] {request.Type} em nível crítico: {val}";
        }
        else if (val >= aviso)
        {
            nivel    = "AVISO";
            mensagem = $"[{request.Zona}] {request.Type} em nível de aviso: {val}";
        }
        else
        {
            nivel    = "NORMAL";
            mensagem = $"[{request.Zona}] {request.Type} em nível normal: {val}";
        }

        return Task.FromResult(new AnalisarResponse
        {
            Valido   = true,
            Nivel    = nivel,
            Mensagem = mensagem,
            Erro     = ""
        });
    }

    public override Task<PolResponse> DetetarPol(PolRequest request, ServerCallContext context)
    {
        double pm25 = ParseOrZero(request.Pm25);
        double pm10 = ParseOrZero(request.Pm10);
        double ar   = ParseOrZero(request.Ar);

        string mensagem;

        if (pm25 > 75 || pm10 > 150)
            mensagem = $"[{request.Zona}] Poluição crítica detetada: PM2.5={pm25} PM10={pm10} AR={ar}";
        else if (pm25 > 35 && pm10 > 50)
            mensagem = $"[{request.Zona}] Padrão de poluição moderada: PM2.5={pm25} PM10={pm10} AR={ar}";
        else if (ar > 100)
            mensagem = $"[{request.Zona}] Qualidade do ar degradada: AR={ar}";
        else
            mensagem = $"[{request.Zona}] Qualidade do ar normal: PM2.5={pm25} PM10={pm10} AR={ar}";

        return Task.FromResult(new PolResponse { Mensagem = mensagem });
    }

    static double ParseOrZero(string s)
    {
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
            return v;
        return 0.0;
    }
}
