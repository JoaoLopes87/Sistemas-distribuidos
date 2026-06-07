using Grpc.Core;
using PreProcessamentoGrpc;
using System.Globalization;

namespace RPC_C.Services;

public class PreProcessamentoServiceImpl
    : PreProcessamentoService.PreProcessamentoServiceBase
{
    public override Task<TimestampResponse>
        ValidarTimestamp(
            TimestampRequest request,
            ServerCallContext context)
    {

        // Accept strict format or common ISO formats (including round-trip / timezone)
        DateTime data;
        bool valido = DateTime.TryParseExact(
            request.Timestamp,
            "yyyy-MM-ddTHH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out data
        );

        if (!valido)
        {
            // fallback to more permissive parse (handles "o" and timezone offsets)
            valido = DateTime.TryParse(request.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out data);
        }

        if (!valido)
        {
            return Task.FromResult(new TimestampResponse
            {
                Valido = false,
                NewTimeStamp = "",
                Erro = "Timestamp inválido"
            });
        }

        return Task.FromResult(new TimestampResponse
        {
            Valido = true,
            NewTimeStamp = data.ToString("yyyy-MM-ddTHH:mm:ss"),
            Erro = ""
        });
    }

    public override Task<ValorResponse> NormalizarValor(
        ValorRequest request,
        ServerCallContext context)
    {
        string type  = request.Type;
        string value = request.Value;

        if (!double.TryParse(value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out double val))
            return Task.FromResult(new ValorResponse
            {
                Valido   = false,
                NewValue = "",
                Erro     = "Value is not numeric"
            });

        (double min, double max) = type switch
        {
            "TEMP"  => (-50.0,   100.0),
            "HUM"   => (  0.0,   100.0),
            "RUIDO" => (  0.0,   200.0),
            "PM2.5" => (  0.0,  1000.0),
            "PM10"  => (  0.0,  1000.0),
            "AR"    => (  0.0,   500.0),
            "LUM"   => (  0.0, 100000.0),
            _       => (double.MinValue, double.MaxValue)
        };

        if (val < min || val > max)
            return Task.FromResult(new ValorResponse
            {
                Valido   = false,
                NewValue = "",
                Erro     = $"Value {val} out of range for type {type}"
            });

        return Task.FromResult(new ValorResponse
        {
            Valido   = true,
            NewValue = val.ToString(CultureInfo.InvariantCulture),
            Erro     = ""
        });
    }

    public override Task<EscalaResponse> ConverterEscala(
        EscalaRequest request,
        ServerCallContext context)
    {
        string lower   = request.Value.Trim().ToLower();
        string numerico = lower;
        float  convertido;

        switch (request.Type)
        {
            case "TEMP":
                if (lower.EndsWith("f"))
                {
                    numerico = lower[..^1];
                    if (!float.TryParse(numerico, NumberStyles.Any, CultureInfo.InvariantCulture, out float fVal))
                        return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                    convertido = (fVal - 32f) * 5f / 9f;
                }
                else if (lower.EndsWith("k"))
                {
                    numerico = lower[..^1];
                    if (!float.TryParse(numerico, NumberStyles.Any, CultureInfo.InvariantCulture, out float kVal))
                        return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                    convertido = kVal - 273.15f;
                }
                else
                {
                    if (lower.EndsWith("c")) numerico = lower[..^1];
                    if (!float.TryParse(numerico, NumberStyles.Any, CultureInfo.InvariantCulture, out float cVal))
                        return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                    convertido = cVal;
                }
                break;

            case "LUM":
                if (lower.EndsWith("lux")) numerico = lower[..^3];
                if (!float.TryParse(numerico, NumberStyles.Any, CultureInfo.InvariantCulture, out float luxVal))
                    return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                convertido = luxVal;
                break;

            case "HUM":
                if (lower.EndsWith("%")) numerico = lower[..^1];
                if (!float.TryParse(numerico, NumberStyles.Any, CultureInfo.InvariantCulture, out float humVal))
                    return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                convertido = humVal;
                break;

            case "RUIDO":
                if (lower.EndsWith("db")) numerico = lower[..^2];
                if (!float.TryParse(numerico, NumberStyles.Any, CultureInfo.InvariantCulture, out float dbVal))
                    return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                convertido = dbVal;
                break;

            default:
                // PM2.5, PM10, AR — só numérico
                if (!float.TryParse(lower, NumberStyles.Any, CultureInfo.InvariantCulture, out float defaultVal))
                    return Task.FromResult(new EscalaResponse { Valido = false, Erro = "Invalid value" });
                convertido = defaultVal;
                break;
        }

        return Task.FromResult(new EscalaResponse { Valido = true, NewValue = convertido });
    }
}
