using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

class MonoInjector
{
    #region WinAPI
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, uint sz, uint t, uint pr);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr p, IntPtr a, uint sz, uint t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, uint sz, out int w);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, uint sz, out int r);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr at, uint st, IntPtr sa, IntPtr pm, uint f, out uint tid);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeThread(IntPtr h, out uint code);

    const uint PROCESS_ALL = 0x1F0FFF;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    const uint PAGE_RWX = 0x40, PAGE_RW = 0x04;
    #endregion

    IntPtr _hProc, _monoBase;

    // Mono export addresses
    IntPtr _getRootDomain, _threadAttach, _asmOpen, _asmGetImage;
    IntPtr _classFromName, _classGetMethod, _runtimeInvoke;

    // Data block layout offsets (all relative to data block start)
    // +0x000: func ptrs (7 * 8 = 56 bytes)
    // +0x038: status int (8 bytes padded)
    // +0x040: dll path string (256 bytes)
    // +0x140: namespace string (64 bytes)
    // +0x180: class name string (64 bytes)
    // +0x1C0: method name string (64 bytes)
    // Total data: 0x200 = 512 bytes

    static void Main(string[] args)
    {
        Console.Title = "GambleDumb Mono Injector";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  +=============================================+
  |   GAMBLE DUMB  -  MONO INJECTOR  v2.0       |
  |   Single-thread shellcode injection         |
  +=============================================+");
        Console.ResetColor();

        string processName = "Gamble With Your Friends";
        string dllPath = ResolveDllPath(args);
        if (dllPath == null) { WaitExit(1); return; }
        Log("DLL", dllPath, ConsoleColor.White);

        Log("WAIT", "Looking for \"" + processName + "\"...", ConsoleColor.Yellow);
        Process proc = WaitForProcess(processName);
        Log("PROC", "Found PID " + proc.Id, ConsoleColor.Green);

        // Find Managed folder and copy DLL there
        string managedDir = FindManagedDir(proc);
        Log("PATH", managedDir, ConsoleColor.DarkGray);

        // Use unique filename to avoid lock from previous injection
        string uniqueName = "GambleDumbMenu_" + DateTime.Now.Ticks.ToString("X") + ".dll";
        string targetDll = Path.Combine(managedDir, uniqueName);
        try
        {
            File.Copy(dllPath, targetDll, false);
            Log("COPY", "DLL -> Managed/" + uniqueName, ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            Log("WARN", "Copy failed: " + ex.Message, ConsoleColor.Yellow);
            // Try game root directory as fallback
            try
            {
                string gameDir = Path.GetDirectoryName(proc.MainModule.FileName);
                targetDll = Path.Combine(gameDir, uniqueName);
                File.Copy(dllPath, targetDll, false);
                Log("COPY", "DLL -> game root/" + uniqueName, ConsoleColor.Green);
            }
            catch
            {
                Log("WARN", "All copy attempts failed, using original path", ConsoleColor.Yellow);
                targetDll = dllPath;
            }
        }

        Log("WAIT", "Letting Mono initialize (5s)...", ConsoleColor.Yellow);
        System.Threading.Thread.Sleep(5000);

        var inj = new MonoInjector();
        try
        {
            inj.Inject(proc, targetDll, "", "Loader", "Init");
            Console.WriteLine();
            Log("OK", "Injection successful! Closing in 1.5 seconds...", ConsoleColor.Green);
            System.Threading.Thread.Sleep(1500);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log("FAIL", ex.Message, ConsoleColor.Red);
            WaitExit(1); return;
        }
    }

    public void Inject(Process proc, string dllPath, string ns, string klass, string method)
    {
        _hProc = OpenProcess(PROCESS_ALL, false, proc.Id);
        if (_hProc == IntPtr.Zero)
            throw new Exception("OpenProcess failed: " + Marshal.GetLastWin32Error() + ". Run as Admin.");

        try
        {
            FindMonoModule(proc);
            ResolveMonoExports();

            // Build the combined data block
            byte[] dataBlock = new byte[0x300];
            WritePtr(dataBlock, 0x00, _getRootDomain);
            WritePtr(dataBlock, 0x08, _threadAttach);
            WritePtr(dataBlock, 0x10, _asmOpen);
            WritePtr(dataBlock, 0x18, _asmGetImage);
            WritePtr(dataBlock, 0x20, _classFromName);
            WritePtr(dataBlock, 0x28, _classGetMethod);
            WritePtr(dataBlock, 0x30, _runtimeInvoke);
            // 0x38 = status (zeroed)
            WriteStr(dataBlock, 0x40, dllPath, 256);
            WriteStr(dataBlock, 0x140, ns, 64);
            WriteStr(dataBlock, 0x180, klass, 64);
            WriteStr(dataBlock, 0x1C0, method, 64);

            // Allocate data block in remote process
            IntPtr remData = RemoteAlloc(0x300, PAGE_RW);
            WriteRemote(remData, dataBlock);
            Log("DATA", "Data block @ 0x" + remData.ToInt64().ToString("X"), ConsoleColor.DarkGray);

            // Build the x64 shellcode that does ALL 7 Mono calls on one thread
            byte[] shellcode = BuildShellcode();
            IntPtr remCode = RemoteAlloc((uint)shellcode.Length, PAGE_RWX);
            WriteRemote(remCode, shellcode);
            Log("CODE", "Shellcode @ 0x" + remCode.ToInt64().ToString("X") + " (" + shellcode.Length + " bytes)", ConsoleColor.DarkGray);

            // Execute: CreateRemoteThread with data block ptr as parameter
            Log("EXEC", "Launching single-thread injection...", ConsoleColor.Yellow);
            uint tid;
            IntPtr hThread = CreateRemoteThread(_hProc, IntPtr.Zero, 0, remCode, remData, 0, out tid);
            if (hThread == IntPtr.Zero)
                throw new Exception("CreateRemoteThread failed: " + Marshal.GetLastWin32Error());

            uint waitResult = WaitForSingleObject(hThread, 30000);
            if (waitResult != 0)
                throw new Exception("Thread timed out (30s). Game may have frozen.");

            uint exitCode = 0;
            GetExitCodeThread(hThread, out exitCode);
            CloseHandle(hThread);

            // Read back the diagnostic pointers
            byte[] diagBytes = new byte[40];
            int diagBytesRead;
            IntPtr diagAddr = new IntPtr(remData.ToInt64() + 0x200);
            if (ReadProcessMemory(_hProc, diagAddr, diagBytes, 40, out diagBytesRead))
            {
                long pDomain = BitConverter.ToInt64(diagBytes, 0);
                long pAssembly = BitConverter.ToInt64(diagBytes, 8);
                long pImage = BitConverter.ToInt64(diagBytes, 16);
                long pKlass = BitConverter.ToInt64(diagBytes, 24);
                long pMethod = BitConverter.ToInt64(diagBytes, 32);
                Log("DIAG", string.Format("Pointers: Domain=0x{0:X}, Assembly=0x{1:X}, Image=0x{2:X}, Class=0x{3:X}, Method=0x{4:X}",
                    pDomain, pAssembly, pImage, pKlass, pMethod), ConsoleColor.DarkGray);
            }

            Log("EXIT", "Thread exit code: " + exitCode + " (0x" + exitCode.ToString("X") + ")", ConsoleColor.DarkGray);

            // Cleanup
            VirtualFreeEx(_hProc, remCode, 0, MEM_RELEASE);
            // Don't free remData - Mono may still reference the path string

            if (exitCode == 2)
                throw new Exception("mono_assembly_open returned NULL. Mono can't load the DLL. Path: " + dllPath);
            if (exitCode == 3)
                throw new Exception("mono_assembly_get_image returned NULL.");
            if (exitCode == 4)
                throw new Exception("mono_class_from_name returned NULL. Class '" + klass + "' not found.");
            if (exitCode == 5)
                throw new Exception("mono_class_get_method_from_name returned NULL. Method '" + method + "' not found.");
            if (exitCode == 0)
                throw new Exception("Shellcode returned 0 (unexpected failure).");
            if (exitCode != 1)
                throw new Exception("Shellcode crashed: 0x" + exitCode.ToString("X"));
        }
        finally
        {
            CloseHandle(_hProc);
        }
    }

    // ═══════════════════════════════════════
    //  BUILD SINGLE x64 SHELLCODE
    // ═══════════════════════════════════════
    // RCX = pointer to data block on entry (from CreateRemoteThread param)
    // Returns 1 on success, 0 on failure
    byte[] BuildShellcode()
    {
        var c = new List<byte>();

        // Prologue - save callee-saved registers
        c.Add(0x55);                                     // push rbp
        c.AddRange(new byte[] { 0x48, 0x89, 0xE5 });    // mov rbp, rsp
        c.Add(0x53);                                     // push rbx
        c.AddRange(new byte[] { 0x41, 0x54 });           // push r12
        c.AddRange(new byte[] { 0x41, 0x55 });           // push r13
        c.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 }); // sub rsp, 0x28 (shadow+align)

        // Save data block ptr: mov r12, rcx
        c.AddRange(new byte[] { 0x49, 0x89, 0xCC });    // mov r12, rcx

        // 1. domain = mono_get_root_domain()
        // call [r12+0x00]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x00 });
        // mov [r12+0x200], rax
        c.AddRange(new byte[] { 0x49, 0x89, 0x84, 0x24, 0x00, 0x02, 0x00, 0x00 });
        // mov r13, rax
        c.AddRange(new byte[] { 0x49, 0x89, 0xC5 });

        // 2. mono_thread_attach(domain)
        // mov rcx, r13
        c.AddRange(new byte[] { 0x4C, 0x89, 0xE9 });
        // call [r12+0x08]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x08 });

        // 3. assembly = mono_domain_assembly_open(domain, path)
        // mov rcx, r13 (domain)
        c.AddRange(new byte[] { 0x4C, 0x89, 0xE9 });
        // lea rdx, [r12+0x40] (path)
        c.AddRange(new byte[] { 0x49, 0x8D, 0x54, 0x24, 0x40 });
        // call [r12+0x10]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x10 });
        // mov [r12+0x208], rax
        c.AddRange(new byte[] { 0x49, 0x89, 0x84, 0x24, 0x08, 0x02, 0x00, 0x00 });
        // mov rbx, rax
        c.AddRange(new byte[] { 0x48, 0x89, 0xC3 });
        // test rax, rax / jz fail
        c.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
        c.AddRange(new byte[] { 0x0F, 0x84 }); // jz rel32
        // Placeholder for fail offset - will patch below
        int jz1_pos = c.Count; c.AddRange(new byte[] { 0, 0, 0, 0 });

        // 4. image = mono_assembly_get_image(assembly)
        // mov rcx, rbx
        c.AddRange(new byte[] { 0x48, 0x89, 0xD9 });
        // call [r12+0x18]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x18 });
        // mov [r12+0x210], rax
        c.AddRange(new byte[] { 0x49, 0x89, 0x84, 0x24, 0x10, 0x02, 0x00, 0x00 });
        // mov r13, rax
        c.AddRange(new byte[] { 0x49, 0x89, 0xC5 });
        // test rax, rax / jz fail
        c.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
        c.AddRange(new byte[] { 0x0F, 0x84 });
        int jz2_pos = c.Count; c.AddRange(new byte[] { 0, 0, 0, 0 });

        // 5. klass = mono_class_from_name(image, ns, classname)
        // mov rcx, r13
        c.AddRange(new byte[] { 0x4C, 0x89, 0xE9 });
        // lea rdx, [r12+0x140] (32-bit displacement needed)
        c.AddRange(new byte[] { 0x49, 0x8D, 0x94, 0x24 });
        c.AddRange(BitConverter.GetBytes((int)0x140));
        // lea r8, [r12+0x180]
        c.AddRange(new byte[] { 0x4D, 0x8D, 0x84, 0x24 });
        c.AddRange(BitConverter.GetBytes((int)0x180));
        // call [r12+0x20]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x20 });
        // mov [r12+0x218], rax
        c.AddRange(new byte[] { 0x49, 0x89, 0x84, 0x24, 0x18, 0x02, 0x00, 0x00 });
        // mov rbx, rax
        c.AddRange(new byte[] { 0x48, 0x89, 0xC3 });
        // test rax, rax / jz fail
        c.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
        c.AddRange(new byte[] { 0x0F, 0x84 });
        int jz3_pos = c.Count; c.AddRange(new byte[] { 0, 0, 0, 0 });

        // 6. method = mono_class_get_method_from_name(klass, name, 0)
        // mov rcx, rbx
        c.AddRange(new byte[] { 0x48, 0x89, 0xD9 });
        // lea rdx, [r12+0x1C0]
        c.AddRange(new byte[] { 0x49, 0x8D, 0x94, 0x24 });
        c.AddRange(BitConverter.GetBytes((int)0x1C0));
        // xor r8d, r8d
        c.AddRange(new byte[] { 0x45, 0x31, 0xC0 });
        // call [r12+0x28]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x28 });
        // mov [r12+0x220], rax
        c.AddRange(new byte[] { 0x49, 0x89, 0x84, 0x24, 0x20, 0x02, 0x00, 0x00 });
        // mov r13, rax
        c.AddRange(new byte[] { 0x49, 0x89, 0xC5 });
        // test rax, rax / jz fail
        c.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
        c.AddRange(new byte[] { 0x0F, 0x84 });
        int jz4_pos = c.Count; c.AddRange(new byte[] { 0, 0, 0, 0 });

        // 7. mono_runtime_invoke(method, NULL, NULL, NULL)
        // mov rcx, r13
        c.AddRange(new byte[] { 0x4C, 0x89, 0xE9 });
        // xor rdx, rdx
        c.AddRange(new byte[] { 0x48, 0x31, 0xD2 });
        // xor r8, r8
        c.AddRange(new byte[] { 0x4D, 0x31, 0xC0 });
        // xor r9, r9
        c.AddRange(new byte[] { 0x4D, 0x31, 0xC9 });
        // call [r12+0x30]
        c.AddRange(new byte[] { 0x41, 0xFF, 0x54, 0x24, 0x30 });

        // Success: mov eax, 1
        c.AddRange(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 });
        // jmp done (will patch)
        c.Add(0xEB);
        int jmpSuccess_pos = c.Count; c.Add(0x00);

        // fail paths with diagnostic exit codes:
        // jz targets for each check - return 2,3,4,5 to identify which call failed
        int fail1_offset = c.Count; // mono_assembly_open failed
        c.AddRange(new byte[] { 0xB8, 0x02, 0x00, 0x00, 0x00 }); // mov eax, 2
        c.Add(0xEB);  // jmp done (will patch)
        int jmp1_pos = c.Count; c.Add(0x00);

        int fail2_offset = c.Count; // mono_assembly_get_image failed
        c.AddRange(new byte[] { 0xB8, 0x03, 0x00, 0x00, 0x00 }); // mov eax, 3
        c.Add(0xEB);
        int jmp2_pos = c.Count; c.Add(0x00);

        int fail3_offset = c.Count; // mono_class_from_name failed
        c.AddRange(new byte[] { 0xB8, 0x04, 0x00, 0x00, 0x00 }); // mov eax, 4
        c.Add(0xEB);
        int jmp3_pos = c.Count; c.Add(0x00);

        int fail4_offset = c.Count; // mono_class_get_method_from_name failed
        c.AddRange(new byte[] { 0xB8, 0x05, 0x00, 0x00, 0x00 }); // mov eax, 5
        // falls through to done

        // done: epilogue
        c.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 }); // add rsp, 0x28
        c.AddRange(new byte[] { 0x41, 0x5D });              // pop r13
        c.AddRange(new byte[] { 0x41, 0x5C });              // pop r12
        c.Add(0x5B);                                         // pop rbx
        c.Add(0x5D);                                         // pop rbp
        c.Add(0xC3);                                         // ret

        // Patch the jz offsets (relative to instruction after the 4-byte displacement)
        byte[] code = c.ToArray();
        PatchJz(code, jz1_pos, fail1_offset);
        PatchJz(code, jz2_pos, fail2_offset);
        PatchJz(code, jz3_pos, fail3_offset);
        PatchJz(code, jz4_pos, fail4_offset);

        // Patch short jmps to done (epilogue starts right after fail4)
        int done_offset = fail4_offset + 5; // after mov eax,5
        code[jmpSuccess_pos] = (byte)(done_offset - (jmpSuccess_pos + 1));
        code[jmp1_pos] = (byte)(done_offset - (jmp1_pos + 1));
        code[jmp2_pos] = (byte)(done_offset - (jmp2_pos + 1));
        code[jmp3_pos] = (byte)(done_offset - (jmp3_pos + 1));

        return code;
    }

    void PatchJz(byte[] code, int dispPos, int targetPos)
    {
        int rel = targetPos - (dispPos + 4); // +4 because displacement is relative to NEXT instruction
        byte[] relBytes = BitConverter.GetBytes(rel);
        code[dispPos] = relBytes[0];
        code[dispPos + 1] = relBytes[1];
        code[dispPos + 2] = relBytes[2];
        code[dispPos + 3] = relBytes[3];
    }

    // ═══════════════════════════════════════
    //  FIND MONO MODULE
    // ═══════════════════════════════════════
    void FindMonoModule(Process proc)
    {
        string[] names = { "mono-2.0-bdwgc.dll", "mono.dll", "mono-2.0-sgen.dll" };
        proc.Refresh();
        foreach (ProcessModule mod in proc.Modules)
        {
            if (names.Any(n => n.Equals(mod.ModuleName, StringComparison.OrdinalIgnoreCase)))
            {
                _monoBase = mod.BaseAddress;
                Log("MONO", mod.ModuleName + " @ 0x" + _monoBase.ToInt64().ToString("X"), ConsoleColor.Cyan);
                return;
            }
        }
        throw new Exception("Mono runtime not found in process modules.");
    }

    // ═══════════════════════════════════════
    //  PARSE PE EXPORTS
    // ═══════════════════════════════════════
    void ResolveMonoExports()
    {
        Log("PE", "Parsing export table...", ConsoleColor.DarkGray);
        byte[] dos = ReadRemote(_monoBase, 64);
        int lfanew = BitConverter.ToInt32(dos, 60);
        byte[] pe = ReadRemote(_monoBase + lfanew, 280);
        if (BitConverter.ToUInt32(pe, 0) != 0x4550) throw new Exception("Bad PE sig.");

        bool x64 = BitConverter.ToUInt16(pe, 24) == 0x20b;
        int dd = x64 ? 24 + 112 : 24 + 96;
        uint expRVA = BitConverter.ToUInt32(pe, dd);
        if (expRVA == 0) throw new Exception("No exports.");

        byte[] ed = ReadRemote(_monoBase + (int)expRVA, 40);
        uint nNames = BitConverter.ToUInt32(ed, 24);
        uint addrRVA = BitConverter.ToUInt32(ed, 28);
        uint nameRVA = BitConverter.ToUInt32(ed, 32);
        uint ordRVA = BitConverter.ToUInt32(ed, 36);

        byte[] namePtrs = ReadRemote(_monoBase + (int)nameRVA, (int)(nNames * 4));
        byte[] ordinals = ReadRemote(_monoBase + (int)ordRVA, (int)(nNames * 2));

        string[] need = { "mono_get_root_domain", "mono_thread_attach", "mono_domain_assembly_open",
            "mono_assembly_get_image", "mono_class_from_name",
            "mono_class_get_method_from_name", "mono_runtime_invoke" };

        var found = new Dictionary<string, IntPtr>();
        for (uint i = 0; i < nNames && found.Count < need.Length; i++)
        {
            uint nRVA = BitConverter.ToUInt32(namePtrs, (int)(i * 4));
            string nm = ReadAscii(_monoBase + (int)nRVA, 64);
            if (Array.IndexOf(need, nm) < 0) continue;
            ushort ord = BitConverter.ToUInt16(ordinals, (int)(i * 2));
            byte[] ab = ReadRemote(_monoBase + (int)addrRVA + ord * 4, 4);
            found[nm] = _monoBase + (int)BitConverter.ToUInt32(ab, 0);
        }

        foreach (string n in need)
            if (!found.ContainsKey(n)) throw new Exception("Export missing: " + n);

        _getRootDomain = found["mono_get_root_domain"];
        _threadAttach = found["mono_thread_attach"];
        _asmOpen = found["mono_domain_assembly_open"];
        _asmGetImage = found["mono_assembly_get_image"];
        _classFromName = found["mono_class_from_name"];
        _classGetMethod = found["mono_class_get_method_from_name"];
        _runtimeInvoke = found["mono_runtime_invoke"];
        Log("PE", "Resolved " + found.Count + " exports", ConsoleColor.Green);
    }

    // ═══════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════
    IntPtr RemoteAlloc(uint sz, uint prot)
    {
        IntPtr a = VirtualAllocEx(_hProc, IntPtr.Zero, sz, MEM_COMMIT | MEM_RESERVE, prot);
        if (a == IntPtr.Zero) throw new Exception("VirtualAllocEx failed.");
        return a;
    }

    void WriteRemote(IntPtr addr, byte[] data)
    {
        int w; if (!WriteProcessMemory(_hProc, addr, data, (uint)data.Length, out w))
            throw new Exception("WriteProcessMemory failed.");
    }

    byte[] ReadRemote(IntPtr addr, int sz)
    {
        byte[] b = new byte[sz]; int r;
        if (!ReadProcessMemory(_hProc, addr, b, (uint)sz, out r))
            throw new Exception("ReadProcessMemory failed at 0x" + addr.ToInt64().ToString("X"));
        return b;
    }

    string ReadAscii(IntPtr addr, int max)
    {
        byte[] b = ReadRemote(addr, max);
        int e = Array.IndexOf<byte>(b, 0); if (e < 0) e = max;
        return Encoding.ASCII.GetString(b, 0, e);
    }

    static void WritePtr(byte[] buf, int off, IntPtr val)
    {
        byte[] b = BitConverter.GetBytes(val.ToInt64());
        Array.Copy(b, 0, buf, off, 8);
    }

    static void WriteStr(byte[] buf, int off, string s, int maxLen)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        int len = Math.Min(b.Length, maxLen - 1);
        Array.Copy(b, 0, buf, off, len);
        buf[off + len] = 0;
    }

    static string FindManagedDir(Process proc)
    {
        try
        {
            string gd = Path.GetDirectoryName(proc.MainModule.FileName);
            string[] tries = {
                Path.Combine(gd, "Gamble With Your Friends_Data", "Managed"),
                Path.Combine(gd, proc.ProcessName + "_Data", "Managed")
            };
            foreach (string t in tries) if (Directory.Exists(t)) return t;
            foreach (string d in Directory.GetDirectories(gd))
            {
                string m = Path.Combine(d, "Managed");
                if (Directory.Exists(m)) return m;
            }
        }
        catch { }
        return @"C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends\Gamble With Your Friends_Data\Managed";
    }

    static string ResolveDllPath(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0])) return Path.GetFullPath(args[0]);
        string h = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GambleDumbMenu.dll");
        if (File.Exists(h)) return h;
        string c = Path.Combine(Directory.GetCurrentDirectory(), "GambleDumbMenu.dll");
        if (File.Exists(c)) return c;
        Log("ERR", "GambleDumbMenu.dll not found.", ConsoleColor.Red);
        return null;
    }

    static Process WaitForProcess(string name)
    {
        while (true)
        {
            var p = Process.GetProcessesByName(name);
            if (p.Length > 0) return p[0];
            System.Threading.Thread.Sleep(500);
        }
    }

    static void Log(string tag, string msg, ConsoleColor color)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("  [");
        Console.ForegroundColor = color; Console.Write(tag);
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("] ");
        Console.ForegroundColor = ConsoleColor.White; Console.WriteLine(msg);
        Console.ResetColor();
    }

    static void WaitExit(int code)
    {
        Console.WriteLine(); Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Press any key to exit..."); Console.ResetColor();
        try
        {
            if (!Console.IsInputRedirected)
            {
                Console.ReadKey(true);
            }
            else
            {
                System.Threading.Thread.Sleep(1000);
            }
        }
        catch { System.Threading.Thread.Sleep(1000); }
        Environment.Exit(code);
    }
}
