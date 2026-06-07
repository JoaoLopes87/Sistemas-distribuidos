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

        string nivel;

        if (val >= critico)
            nivel = "CRITICO";
        else if (val >= aviso)
            nivel = "AVISO";
        else
            nivel = "NORMAL";

        string stats = "";
        if (!string.IsNullOrEmpty(request.Min) && !string.IsNullOrEmpty(request.Max) && !string.IsNullOrEmpty(request.Media))
            stats = $" | Min={request.Min} Max={request.Max} Média={request.Media}";

        string mensagem = $"[{request.Zona}] {request.Type} {nivel}: {val}{stats}";

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

    public override Task<RiscoResponse> PrevRisco(RiscoRequest request, ServerCallContext context)
    {
        bool temTemp = !string.IsNullOrEmpty(request.Temp);
        bool temHum  = !string.IsNullOrEmpty(request.Hum);
        bool temPm25 = !string.IsNullOrEmpty(request.Pm25);
        bool temAr   = !string.IsNullOrEmpty(request.Ar);

        string mensagem;

        if (temTemp && temHum)
        {
            double temp = ParseOrZero(request.Temp);
            double hum  = ParseOrZero(request.Hum);

            if (temp >= 40 && hum >= 70)
                mensagem = $"[{request.Zona}] Risco ALTO de stress térmico: TEMP={temp} HUM={hum}";
            else if (temp >= 30 && hum >= 60)
                mensagem = $"[{request.Zona}] Risco MEDIO de stress térmico: TEMP={temp} HUM={hum}";
            else
                mensagem = $"[{request.Zona}] Risco BAIXO de stress térmico: TEMP={temp} HUM={hum}";
        }
        else if (temPm25 && temAr)
        {
            double pm25 = ParseOrZero(request.Pm25);
            double ar   = ParseOrZero(request.Ar);

            if (pm25 > 75 && ar > 200)
                mensagem = $"[{request.Zona}] Risco ALTO respiratório: PM2.5={pm25} AR={ar}";
            else if (pm25 > 35 || ar > 100)
                mensagem = $"[{request.Zona}] Risco MEDIO respiratório: PM2.5={pm25} AR={ar}";
            else
                mensagem = $"[{request.Zona}] Risco BAIXO respiratório: PM2.5={pm25} AR={ar}";
        }
        else
        {
            mensagem = $"[{request.Zona}] Dados insuficientes para previsão de risco";
        }

        return Task.FromResult(new RiscoResponse { Mensagem = mensagem });
    }

    static double ParseOrZero(string s)
    {
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
            return v;
        return 0.0;
    }
}
