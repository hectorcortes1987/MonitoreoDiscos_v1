using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;

class DiscoConfig
{
    public string Unidad { get; set; }

    public string Nombre { get; set; }

    public double PorcentajeAlerta { get; set; }
}

class Program
{
    static IConfiguration config;

    static void Main()
    {
        config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(
                "appsettings.json",
                optional: false
            )
            .Build();

        try
        {
            EscribirLog(
                "==== INICIO MONITOREO ===="
            );

            string configDiscos =
                config["DiscosMonitoreados"];

            Dictionary<string, DiscoConfig>
                discosConfig =
                    ObtenerConfiguracionDiscos(
                        configDiscos
                    );

            foreach (DriveInfo drive
                in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                string unidad =
                    drive.Name.Replace("\\", "");

                if (!discosConfig.ContainsKey(unidad))
                    continue;

                DiscoConfig disco =
                    discosConfig[unidad];

                double porcentajeLibre =
                    (drive.TotalFreeSpace * 100.0)
                    / drive.TotalSize;

                double totalGB =
                    drive.TotalSize /
                    1024.0 / 1024 / 1024;

                double libreGB =
                    drive.TotalFreeSpace /
                    1024.0 / 1024 / 1024;

                double usadoGB =
                    totalGB - libreGB;

                Console.WriteLine(
                    $"{disco.Nombre} ({unidad}) - " +

                    $"Libre: {porcentajeLibre:F2}% | " +

                    $"Libre GB: {libreGB:F2} GB | " +

                    $"Total GB: {totalGB:F2} GB"
                );

                EscribirLog(
                    $"{disco.Nombre} ({unidad}) - " +

                    $"Libre: {porcentajeLibre:F2}% | " +

                    $"Libre GB: {libreGB:F2} GB | " +

                    $"Total GB: {totalGB:F2} GB"
                );

                if (porcentajeLibre <
                    disco.PorcentajeAlerta)
                {
                    EnviarAlerta(
                        disco.Nombre,
                        unidad,
                        porcentajeLibre,
                        disco.PorcentajeAlerta,
                        totalGB,
                        libreGB,
                        usadoGB
                    );

                    EscribirLog(
                        $"ALERTA enviada para " +
                        $"{disco.Nombre} ({unidad})"
                    );
                }
            }

            EscribirLog(
                "==== FIN MONITOREO ===="
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "ERROR GENERAL: " +
                ex.Message
            );

            EscribirLog(
                "ERROR GENERAL: " +
                ex.ToString()
            );
        }
    }

    static Dictionary<string, DiscoConfig>
        ObtenerConfiguracionDiscos(
            string configDiscos)
    {
        Dictionary<string, DiscoConfig>
            discos =
                new Dictionary<string,
                    DiscoConfig>();

        string[] items =
            configDiscos.Split(';');

        foreach (string item in items)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            string[] partes =
                item.Split('|');

            if (partes.Length != 3)
                continue;

            string unidad =
                partes[0].Trim();

            string nombre =
                partes[1].Trim();

            double porcentaje =
                Convert.ToDouble(
                    partes[2]
                );

            discos[unidad] =
                new DiscoConfig
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
            string origen =
                config["CorreoOrigen"];

            string destino =
                config["CorreoDestino"];

            string smtpServer =
                config["SMTPServer"];

            int puerto =
                Convert.ToInt32(
                    config["SMTPPuerto"]
                );

            string user =
                config["SMTPUser"];

            string password =
                config["SMTPPassword"];

            string cliente =
                config["NombreCliente"];

            string servidor =
                Environment.MachineName;

            string ipServidor =
                ObtenerIP();

            MailMessage mail =
                new MailMessage();

            mail.From =
                new MailAddress(origen);

            string[] correos =
                destino.Split(';');

            foreach (string correo
                in correos)
            {
                if (!string.IsNullOrWhiteSpace(
                    correo))
                {
                    mail.To.Add(
                        correo.Trim()
                    );
                }
            }

            mail.Priority =
                MailPriority.High;

            mail.Subject =
                $"⚠️ [{cliente}] " +
                $"{servidor} - " +
                $"Disco {nombreDisco} ({unidad})";

            mail.Body =
                $"CLIENTE: {cliente}\n" +

                $"SERVIDOR: {servidor}\n" +

                $"IP: {ipServidor}\n\n" +

                $"DISCO: {nombreDisco}\n" +

                $"UNIDAD: {unidad}\n\n" +

                $"ESPACIO TOTAL: " +
                $"{totalGB:F2} GB\n" +

                $"ESPACIO USADO: " +
                $"{usadoGB:F2} GB\n" +

                $"ESPACIO LIBRE: " +
                $"{libreGB:F2} GB\n" +

                $"PORCENTAJE LIBRE: " +
                $"{porcentajeLibre:F2}%\n\n" +

                $"UMBRAL CONFIGURADO: " +
                $"{porcentajeAlerta:F2}%\n\n" +

                $"FECHA: {DateTime.Now}";

            SmtpClient smtp =
                new SmtpClient(
                    smtpServer,
                    puerto
                )
                {
                    Credentials =
                        new NetworkCredential(
                            user,
                            password
                        ),

                    EnableSsl = true
                };

            smtp.Send(mail);

            Console.WriteLine(
                $"⚠️ Alerta enviada para " +
                $"{nombreDisco}"
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "ERROR SMTP: " +
                ex.Message
            );

            EscribirLog(
                "ERROR SMTP: " +
                ex.ToString()
            );
        }
    }

    static string ObtenerIP()
    {
        try
        {
            string host =
                Dns.GetHostName();

            IPHostEntry ipEntry =
                Dns.GetHostEntry(host);

            foreach (IPAddress ip
                in ipEntry.AddressList)
            {
                if (ip.AddressFamily ==
                    AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }

            return "IP_NO_ENCONTRADA";
        }
        catch
        {
            return "ERROR_IP";
        }
    }

    static void EscribirLog(
        string mensaje)
    {
        try
        {
            string carpeta =
                config["RutaLog"];

            if (!Directory.Exists(carpeta))
            {
                Directory.CreateDirectory(
                    carpeta
                );
            }

            string rutaLog =
                Path.Combine(
                    carpeta,
                    $"MonitorDiscos_" +
                    $"{DateTime.Now:yyyyMMdd}.log"
                );

            string linea =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - " +
                $"{mensaje}";

            File.AppendAllText(
                rutaLog,
                linea + Environment.NewLine
            );
        }
        catch
        {
        }
    }
}