using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using MimeKit;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;

// ===================== MODELOS DE CONFIGURACIÓN =====================

class DiscosConfig
{
    public double PorcentajeAlertaDefault { get; set; } = 15;
    public List<string> Excluir { get; set; } = new();

    // Overrides opcionales de nombre/umbral por unidad.
    // Formato: "Unidad|Nombre|UmbralPorcentaje;Unidad|Nombre|UmbralPorcentaje;..."
    // Cualquier unidad NO listada aquí se monitorea igual (de forma automática),
    // usando el umbral por defecto y su etiqueta de volumen de Windows.
    public string Personalizados { get; set; } = "";
}

class UptimeConfig
{
    // 0 = deshabilita la alerta de reinicio reciente
    public int AlertarSiReinicioRecienteMinutos { get; set; } = 0;
}

class SqlServerConfig
{
    // Nombre del servicio de Windows. Para instancia default: "MSSQLSERVER".
    // Para instancia nombrada: "MSSQL$NombreInstancia".
    public string NombreServicio { get; set; } = "";

    // Puede incluir el marcador {SQL_PASSWORD}, que se reemplaza con la
    // variable de entorno SQL_PASSWORD en tiempo de ejecución.
    public string ConnectionString { get; set; } = "";

    public List<string> BasesMonitoreadas { get; set; } = new();
}

class AppConfig
{
    public string NombreCliente { get; set; } = "";
    public DiscosConfig Discos { get; set; } = new();
    public UptimeConfig Uptime { get; set; } = new();
    public SqlServerConfig SqlServer { get; set; } = new();

    public string RutaLog { get; set; } = "Logs";

    public string CorreoOrigen { get; set; } = "";
    public string CorreoDestino { get; set; } = "";
    public string SMTPServer { get; set; } = "";
    public int SMTPPuerto { get; set; } = 587;
    public string SMTPUser { get; set; } = "";
    public string SMTPPassword { get; set; } = "";
}

class Program
{
    static AppConfig cfg = null!;

    static void Main()
    {
        IConfiguration raw = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        cfg = new AppConfig();
        raw.Bind(cfg);

        var problemas = new List<string>();

        try
        {
            EscribirLog("==== INICIO MONITOREO ====");

            problemas.AddRange(MonitorearDiscos());
            problemas.AddRange(MonitorearUptime());
            problemas.AddRange(MonitorearSqlServer());

            if (problemas.Count > 0)
            {
                EnviarAlerta(problemas);
                EscribirLog($"ALERTA enviada ({problemas.Count} problema(s) detectado(s))");
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

    // ===================== DISCOS (DETECCIÓN DINÁMICA) =====================
    // Ya no es necesario listar cada unidad manualmente: se recorren todas las
    // unidades fijas que Windows reporte como listas (IsReady) y se les aplica
    // el umbral por defecto, salvo que exista un umbral/nombre personalizado.
    static List<string> MonitorearDiscos()
    {
        var problemas = new List<string>();
        var personalizados = ParsearPersonalizados(cfg.Discos.Personalizados);

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                continue;

            string unidad = drive.Name.Replace("\\", ""); // "C:" en vez de "C:\"

            if (cfg.Discos.Excluir.Contains(unidad, StringComparer.OrdinalIgnoreCase))
                continue;

            bool tienePersonalizado = personalizados.TryGetValue(unidad, out var p);

            string nombre = tienePersonalizado
                ? p.Nombre
                : (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? unidad : drive.VolumeLabel);

            double umbral = tienePersonalizado ? p.Umbral : cfg.Discos.PorcentajeAlertaDefault;

            double porcentajeLibre = (drive.TotalFreeSpace * 100.0) / drive.TotalSize;
            double totalGB = drive.TotalSize / 1024.0 / 1024 / 1024;
            double libreGB = drive.TotalFreeSpace / 1024.0 / 1024 / 1024;
            double usadoGB = totalGB - libreGB;

            string linea =
                $"{nombre} ({unidad}) - Libre: {porcentajeLibre:F2}% | " +
                $"Libre: {libreGB:F2} GB | Total: {totalGB:F2} GB";

            Console.WriteLine(linea);
            EscribirLog(linea);

            if (porcentajeLibre < umbral)
            {
                problemas.Add(
                    $"[DISCO] {nombre} ({unidad})\n" +
                    $"  Libre: {porcentajeLibre:F2}% (umbral: {umbral:F2}%)\n" +
                    $"  Total: {totalGB:F2} GB | Usado: {usadoGB:F2} GB | Libre: {libreGB:F2} GB");
            }
        }

        return problemas;
    }

    // Parsea "Unidad|Nombre|Umbral;Unidad|Nombre|Umbral;..." al mismo estilo
    // que usaba la configuración original de esta app.
    static Dictionary<string, (string Nombre, double Umbral)> ParsearPersonalizados(string valor)
    {
        var resultado = new Dictionary<string, (string Nombre, double Umbral)>(
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(valor))
            return resultado;

        foreach (string item in valor.Split(';'))
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
                out double umbral))
                continue;

            resultado[unidad] = (nombre, umbral);
        }

        return resultado;
    }

    // ===================== TIEMPO ACTIVO DEL SERVIDOR =====================
    static List<string> MonitorearUptime()
    {
        var problemas = new List<string>();

        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        DateTime horaArranque = DateTime.Now - uptime;

        string texto =
            $"Tiempo activo: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m " +
            $"(último arranque aprox: {horaArranque:yyyy-MM-dd HH:mm:ss})";

        Console.WriteLine(texto);
        EscribirLog(texto);

        int limite = cfg.Uptime.AlertarSiReinicioRecienteMinutos;
        if (limite > 0 && uptime.TotalMinutes < limite)
        {
            problemas.Add(
                $"[SERVIDOR] Reinicio reciente detectado.\n" +
                $"  Tiempo activo: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m " +
                $"(umbral configurado: {limite} min)\n" +
                $"  Último arranque aprox: {horaArranque:yyyy-MM-dd HH:mm:ss}");
        }

        return problemas;
    }

    // ===================== ESTATUS DE SQL SERVER =====================
    static List<string> MonitorearSqlServer()
    {
        var problemas = new List<string>();
        SqlServerConfig sqlCfg = cfg.SqlServer;

        // 1. Estado del servicio de Windows (arrancado / detenido / etc.)
        if (!string.IsNullOrWhiteSpace(sqlCfg.NombreServicio))
        {
            try
            {
                using var sc = new ServiceController(sqlCfg.NombreServicio);
                string estado = sc.Status.ToString();

                string linea = $"Servicio '{sqlCfg.NombreServicio}': {estado}";
                Console.WriteLine(linea);
                EscribirLog(linea);

                if (sc.Status != ServiceControllerStatus.Running)
                {
                    problemas.Add(
                        $"[SQL SERVER] El servicio '{sqlCfg.NombreServicio}' no está en ejecución.\n" +
                        $"  Estado actual: {estado}");
                }
            }
            catch (Exception ex)
            {
                string msg = $"No se pudo consultar el servicio '{sqlCfg.NombreServicio}': {ex.Message}";
                Console.Error.WriteLine(msg);
                EscribirLog("ERROR SERVICIO SQL: " + ex.ToString());
                problemas.Add($"[SQL SERVER] {msg}");
            }
        }

        // 2. Conectividad real y estado de cada base monitoreada
        if (!string.IsNullOrWhiteSpace(sqlCfg.ConnectionString))
        {
            try
            {
                string sqlPassword = Environment.GetEnvironmentVariable("SQL_PASSWORD") ?? "";
                string connStr = string.IsNullOrEmpty(sqlPassword)
                    ? sqlCfg.ConnectionString
                    : sqlCfg.ConnectionString.Replace("{SQL_PASSWORD}", sqlPassword);

                using var conn = new SqlConnection(connStr);
                conn.Open();

                EscribirLog("Conexión a SQL Server: OK");

                foreach (string nombreBase in sqlCfg.BasesMonitoreadas)
                {
                    string estadoBase = ObtenerEstadoBaseDatos(conn, nombreBase);

                    string linea = $"Base de datos '{nombreBase}': {estadoBase}";
                    Console.WriteLine(linea);
                    EscribirLog(linea);

                    if (!string.Equals(estadoBase, "ONLINE", StringComparison.OrdinalIgnoreCase))
                    {
                        problemas.Add(
                            $"[SQL SERVER] La base de datos '{nombreBase}' no está en línea.\n" +
                            $"  Estado: {estadoBase}");
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = $"No se pudo conectar a SQL Server: {ex.Message}";
                Console.Error.WriteLine(msg);
                EscribirLog("ERROR CONEXION SQL: " + ex.ToString());
                problemas.Add($"[SQL SERVER] {msg}");
            }
        }

        return problemas;
    }

    static string ObtenerEstadoBaseDatos(SqlConnection conn, string nombreBase)
    {
        const string query = "SELECT state_desc FROM sys.databases WHERE name = @nombre";

        using var cmd = new SqlCommand(query, conn);
        cmd.Parameters.AddWithValue("@nombre", nombreBase);

        object? resultado = cmd.ExecuteScalar();

        return resultado?.ToString() ?? "NO_ENCONTRADA";
    }

    // ===================== ENVÍO DE ALERTA (CONSOLIDADA) =====================
    static void EnviarAlerta(List<string> problemas)
    {
        try
        {
            string origen     = cfg.CorreoOrigen;
            string destino    = cfg.CorreoDestino;
            string smtpServer = cfg.SMTPServer;
            int    puerto     = cfg.SMTPPuerto;
            string user       = cfg.SMTPUser;
            string cliente    = cfg.NombreCliente;

            string password =
                Environment.GetEnvironmentVariable("SMTP_PASSWORD")
                ?? cfg.SMTPPassword
                ?? "";

            string servidor   = Environment.MachineName;
            string ipServidor = ObtenerIP();

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(origen));
            message.Priority = MessagePriority.Urgent;
            message.Subject =
                $"⚠️ [{cliente}] {servidor} - {problemas.Count} problema(s) detectado(s)";

            foreach (string correo in destino.Split(';'))
            {
                if (!string.IsNullOrWhiteSpace(correo))
                    message.To.Add(MailboxAddress.Parse(correo.Trim()));
            }

            string cuerpo =
                $"CLIENTE: {cliente}\n" +
                $"SERVIDOR: {servidor}\n" +
                $"IP: {ipServidor}\n" +
                $"FECHA: {DateTime.Now}\n\n" +
                string.Join("\n\n", problemas);

            message.Body = new TextPart("plain") { Text = cuerpo };

            using var smtp = new SmtpClient();
            smtp.Connect(smtpServer, puerto, SecureSocketOptions.StartTls);
            smtp.Authenticate(user, password);
            smtp.Send(message);
            smtp.Disconnect(true);

            Console.WriteLine($"⚠️ Alerta enviada con {problemas.Count} problema(s)");
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
            string carpeta = cfg.RutaLog;

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
            string carpeta = cfg.RutaLog;

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
