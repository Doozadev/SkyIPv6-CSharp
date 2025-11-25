// Services/Logger.cs
namespace CfDdnsClient.Services
{
    public static class Logger
    {
        // 确保日志格式与 Go 版本对齐，方便调试

        private static void Log(string level, string message, ConsoleColor color)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"[{timestamp}] ");
            Console.ForegroundColor = color;
            Console.WriteLine($"{level}: {message}");
            Console.ResetColor();
        }

        public static void Info(string format, params object[] args)
        {
            Log("INFO", string.Format(format, args), ConsoleColor.Cyan);
        }

        public static void Error(string format, params object[] args)
        {
            Log("ERROR", string.Format(format, args), ConsoleColor.Red);
        }
        public static void Warning(string format, params object[] args) =>
            Log("WARNING", string.Format(format, args), ConsoleColor.Yellow);
        public static void Success(string format, params object[] args)
        {
            Log("SUCCESS", string.Format(format, args), ConsoleColor.Green);
        }

        public static void Fatal(string format, params object[] args)
        {
            // 致命错误，退出程序
            Log("FATAL", string.Format(format, args), ConsoleColor.DarkRed);
            Environment.Exit(1);
        }
    }
}
