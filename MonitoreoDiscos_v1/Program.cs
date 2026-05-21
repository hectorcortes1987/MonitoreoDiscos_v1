using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;

class DiscoConfig
{
    public string Unidad { get; set; } = "";
    public string Nombre { get; set; } = "";
    public double PorcentajeAlerta { get; set; }
}

class Program
{
    static IConfiguration config = null!;

    static void Main()
    {
        config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        try
        {
            EscribirLog("==== INICIO MONITOREO ====");

            string configDiscos = config["DiscosMonitoreados"]
                ?? throw new InvalidOperationException(
                    "DiscosMonitoreados no configurado.");

            Dictionary<string, DiscoConfig> discosConfig =
                ObtenerConfiguracionDiscos(configDiscos);

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                string unidad = drive.Name.Replace("\\", "");

                if (!discosConfig.ContainsKey(unidad))
                    continue;

                DiscoConfig disco = discosConfig[unidad];

                double porcentajeLibre =
                    (drive.TotalFreeSpace * 100.0) / drive.TotalSize;

                double totalGB = drive.TotalSize / 1024.0 / 1024 / 1024;
                double libreGB = drive.TotalFreeSpace / 1024.0 / 1024 / 1024;
                double usadoGB = totalGB - libreGB;

                string linea =
                    $"{disco.Nombre} ({unidad}) - " +
                    $"Libre: {porcentajeLibre:F2}% | " +
                    $"Libre GB: {libreGB:F2} GB | " +
                    $"Total GB: {totalGB:F2} GB";

                Console.WriteLine(linea);
                EscribirLog(linea);

                if (porcentajeLibre < disco.PorcentajeAlerta)
                {
                    EnviarAlerta(
                        disco.Nombre, unidad,
                        porcentajeLibre, disco.PorcentajeAlerta,
                        totalGB, libreGB, usadoGB);

                    EscribirLog(
                        $"ALERTA enviada para {disco.Nombre} ({unidad})");
                }
            }

            LimpiarLogsAntiguos();

            EscribirLog("==== FIN MONITOREO ====");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR GENERAL: " + ex.Message);
            EscribirLog("ERROR GENERAL: " + ex.ToString());
        }
    }

    static Dictionary<string, DiscoConfig> ObtenerConfiguracionDiscos(
        string configDiscos)
    {
        var discos = new Dictionary<string, DiscoConfig>();

        foreach (string item in configDiscos.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            string[] partes = item.Split('|');

            if (partes.Length != 3)
                continue;

            string unidad = partes[0].Trim();
            string nombre = partes[1].Trim();

            if (!double.TryParse(
                partes[2].Trim(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double porcentaje))
            {
                Console.Error.WriteLine(
                    $"Porcentaje inválido para {unidad}: '{partes[2]}'");
                continue;
            }

            discos[unidad] = new DiscoConfig
            {
                Unidad = unidad,
                Nombre = nombre,
                PorcentajeAlerta = porcentaje
            };
        }

        return discos;
    }

    static void EnviarAlerta(
        string nombreDisco,
        string unidad,
        double porcentajeLibre,
        double porcentajeAlerta,
        double totalGB,
        double libreGB,
        double usadoGB)
    {
        try
        {
            string origen     = config["CorreoOrigen"]   ?? "";
            string destino    = config["CorreoDestino"]  ?? "";
            string smtpServer = config["SMTPServer"]     ?? "";
            int    puerto     = int.Parse(
                config["SMTPPuerto"] ?? "587",
                CultureInfo.InvariantCulture);
            string user    = config["SMTPUser"]     ?? "";
            string cliente = config["NombreCliente"] ?? "";

            // Lee la contraseña desde variable de entorno; el valor en
            // appsettings.json solo se usa como fallback en desarrollo local
            string password =
                Environment.GetEnvironmentVariable("SMTP_PASSWORD")
                ?? config["SMTPPassword"]
                ?? "";

            string servidor   = Environment.MachineName;
            string ipServidor = ObtenerIP();

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(origen));
            message.Priority = MessagePriority.Urgent;
            message.Subject =
                $"⚠️ [{cliente}] {servidor} - Disco {nombreDisco} ({unidad})";

            foreach (string correo in destino.Split(';'))
            {
                if (!string.IsNullOrWhiteSpace(correo))
                    message.To.Add(MailboxAddress.Parse(correo.Trim()));
            }

            message.Body = new TextPart("plain")
            {
                Text =
                    $"CLIENTE: {cliente}\n"                    +
                    $"SERVIDOR: {servidor}\n"                  +
                    $"IP: {ipServidor}\n\n"                    +
                    $"DISCO: {nombreDisco}\n"                  +
                    $"UNIDAD: {unidad}\n\n"                    +
                    $"ESPACIO TOTAL: {totalGB:F2} GB\n"        +
                    $"ESPACIO USADO: {usadoGB:F2} GB\n"        +
                    $"ESPACIO LIBRE: {libreGB:F2} GB\n"        +
                    $"PORCENTAJE LIBRE: {porcentajeLibre:F2}%\n\n" +
                    $"UMBRAL CONFIGURADO: {porcentajeAlerta:F2}%\n\n" +
                    $"FECHA: {DateTime.Now}"
            };

            using var smtp = new SmtpClient();
            smtp.Connect(smtpServer, puerto, SecureSocketOptions.StartTls);
            smtp.Authenticate(user, password);
            smtp.Send(message);
            smtp.Disconnect(true);

            Console.WriteLine($"⚠️ Alerta enviada para {nombreDisco}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR SMTP: " + ex.Message);
            EscribirLog("ERROR SMTP: " + ex.ToString());
        }
    }

    // Detecta la primera IP activa ignorando loopback y túneles,
    // evitando retornar IPs incorrectas en servidores con múltiples NICs
    static string ObtenerIP()
    {
        try
        {
            foreach (NetworkInterface ni in
                NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up          ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                foreach (UnicastIPAddressInformation addr in
                    ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily ==
                        AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }

            return "IP_NO_ENCONTRADA";
        }
        catch
        {
            return "ERROR_IP";
        }
    }

    static void EscribirLog(string mensaje)
    {
        try
        {
            string carpeta = config["RutaLog"] ?? "Logs";

            if (!Directory.Exists(carpeta))
                Directory.CreateDirectory(carpeta);

            string rutaLog = Path.Combine(
                carpeta,
                $"MonitorDiscos_{DateTime.Now:yyyyMMdd}.log");

            string linea =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {mensaje}";

            File.AppendAllText(rutaLog, linea + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR escribiendo log: {ex.Message}");
        }
    }

    // Elimina archivos de log con más de diasRetencion días
    static void LimpiarLogsAntiguos(int diasRetencion = 30)
    {
        try
        {
            string carpeta = config["RutaLog"] ?? "Logs";

            if (!Directory.Exists(carpeta))
                return;

            DateTime limite = DateTime.Now.AddDays(-diasRetencion);

            foreach (string archivo in
                Directory.GetFiles(carpeta, "MonitorDiscos_*.log"))
            {
                if (File.GetLastWriteTime(archivo) < limite)
                    File.Delete(archivo);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR limpiando logs: {ex.Message}");
        }
    }
}
