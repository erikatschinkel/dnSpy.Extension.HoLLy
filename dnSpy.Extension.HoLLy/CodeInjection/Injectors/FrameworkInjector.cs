using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Iced.Intel;

namespace HoLLy.dnSpyExtension.CodeInjection.Injectors
{
    internal class FrameworkV2Injector : FrameworkInjector
    {
        protected override string ClrVersion => "v2.0.50727";
    }

    internal class FrameworkV4Injector : FrameworkInjector
    {
        protected override string ClrVersion => "v4.0.30319";
    }

    internal abstract class FrameworkInjector : IInjector
    {
        protected abstract string ClrVersion { get; }
        public Action<string> Log { private get; set; } = s => { };

        public void Inject(int pid, string path, string typeName, string methodName, string? parameter, bool x86)
        {
            IntPtr hProc = Native.OpenProcess(Native.ProcessAccessFlags.AllForDllInject, false, pid);
            if (hProc == IntPtr.Zero)
                throw new Exception("Couldn't open process");

            Log("Handle: " + hProc.ToInt32().ToString("X8"));

            var bindToRuntimeAddr = GetCorBindToRuntimeExAddress(pid, hProc, x86);
            Log("CurBindToRuntimeEx: " + bindToRuntimeAddr.ToInt64().ToString("X8"));

            var hStub = AllocateStub(hProc, path, typeName, methodName, parameter, bindToRuntimeAddr, x86, ClrVersion);
            Log("Created stub at: " + hStub.ToInt64().ToString("X8"));

            var hThread = Native.CreateRemoteThread(hProc, IntPtr.Zero, 0u, hStub, IntPtr.Zero, 0u, IntPtr.Zero);
            Log("Thread handle: " + hThread.ToInt32().ToString("X8"));

            var success = Native.GetExitCodeThread(hThread, out IntPtr exitCode);
            Log("GetExitCode success: " + success);
            Log("Exit code: " + exitCode.ToInt32().ToString("X8"));

            Native.CloseHandle(hProc);
        }

        private static IntPtr GetCorBindToRuntimeExAddress(int pid, IntPtr hProc, bool x86)
        {
            var proc = Process.GetProcessById(pid);
            var mod = proc.Modules.OfType<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("mscoree.dll", StringComparison.InvariantCultureIgnoreCase));

            if (mod is null)
                throw new Exception("Couldn't find MSCOREE.DLL, arch mismatch?");

            int fnAddr = CodeInjectionUtils.GetExportAddress(hProc, mod.BaseAddress, "CorBindToRuntimeEx", x86);

            return mod.BaseAddress + fnAddr;
        }

        private IntPtr AllocateStub(IntPtr hProc, string asmPath, string typeName, string methodName, string? args, IntPtr fnAddr, bool x86, string clrVersion)
        {
            const string buildFlavor = "wks";    // WorkStation

            var clsidCLRRuntimeHost = new Guid(0x90F1A06E, 0x7712, 0x4762, 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02);
            var  iidICLRRuntimeHost = new Guid(0x90F1A06C, 0x7712, 0x4762, 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02);

            IntPtr ppv = alloc(IntPtr.Size);
            IntPtr riid = allocBytes(iidICLRRuntimeHost.ToByteArray());
            IntPtr rcslid = allocBytes(clsidCLRRuntimeHost.ToByteArray());
            IntPtr pwszBuildFlavor = allocString(buildFlavor);
            IntPtr pwszVersion = allocString(clrVersion);

            IntPtr pReturnValue = alloc(4);
            IntPtr pwzArgument = allocString(args);
            IntPtr pwzMethodName = allocString(methodName);
            IntPtr pwzTypeName = allocString(typeName);
            IntPtr pwzAssemblyPath = allocString(asmPath);

            var instructions = new InstructionList();

            void addCall(Register r, params object[] callArgs)
            {
                foreach (Instruction instr in CodeInjectionUtils.CreateCallStub(r, callArgs, out _, x86))
                    instructions.Add(instr);
            }

            if (x86) {
                // call CorBindtoRuntimeEx
                instructions.Add(Instruction.Create(Code.Mov_r32_imm32, Register.EAX, fnAddr.ToInt32()));
                addCall(Register.EAX, pwszVersion, pwszBuildFlavor, (byte)0, rcslid, riid, ppv);

                // call ICLRRuntimeHost::Start
                instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EDX, new MemoryOperand(Register.None, ppv.ToInt32())));
                instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EDX)));
                instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EAX, 0x0C)));
                addCall(Register.EAX, Register.EDX);

                // call ICLRRuntimeHost::ExecuteInDefaultAppDomain
                instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EDX, new MemoryOperand(Register.None, ppv.ToInt32())));
                instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EDX)));
                instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EAX, 0x2C)));
                addCall(Register.EAX, Register.EDX, pwzAssemblyPath, pwzTypeName, pwzMethodName, pwzArgument, pReturnValue);

                instructions.Add(Instruction.Create(Code.Retnd));
            } else {
                const int maxStackIndex = 3;
                const int stackOffset = 0x20;
                MemoryOperand stackAccess(int i) => new MemoryOperand(Register.RSP, stackOffset + i * 8);

                instructions.Add(Instruction.Create(Code.Sub_rm64_imm8, Register.RSP, 0x20 + maxStackIndex * 8));

                // call CorBindtoRuntimeEx
                // calling convention: https://docs.microsoft.com/en-us/cpp/build/x64-calling-convention?view=vs-2019
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RAX, ppv.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_rm64_r64, stackAccess(1), Register.RAX));
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RAX, riid.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_rm64_r64, stackAccess(0), Register.RAX));

                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.R9, rcslid.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r32_imm32, Register.R8D, 0));    // startupFlags
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RDX, pwszBuildFlavor.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RCX, pwszVersion.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RAX, fnAddr.ToInt64()));
                instructions.Add(Instruction.Create(Code.Call_rm64, Register.RAX));

                // call pClrHost->Start();
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RCX, ppv.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RCX, new MemoryOperand(Register.RCX)));
                instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RAX, new MemoryOperand(Register.RCX)));
                instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RDX, new MemoryOperand(Register.RAX, 0x18)));
                instructions.Add(Instruction.Create(Code.Call_rm64, Register.RDX));

                // call pClrHost->ExecuteInDefaultAppDomain()
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RDX, pReturnValue.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_rm64_r64, stackAccess(1), Register.RDX));
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RDX, pwzArgument.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_rm64_r64, stackAccess(0), Register.RDX));

                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.R9, pwzMethodName.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.R8, pwzTypeName.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RDX, pwzAssemblyPath.ToInt64()));

                instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RCX, ppv.ToInt64()));
                instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RCX, new MemoryOperand(Register.RCX)));
                instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RAX, new MemoryOperand(Register.RCX)));
                instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RAX, new MemoryOperand(Register.RAX, 0x58)));
                instructions.Add(Instruction.Create(Code.Call_rm64, Register.RAX));

                instructions.Add(Instruction.Create(Code.Add_rm64_imm8, Register.RSP, 0x20 + maxStackIndex * 8));

                instructions.Add(Instruction.Create(Code.Retnq));
            }

            Log("Instructions to be injected:\n" + string.Join("\n", instructions));

            var cw = new CodeWriterImpl();
            var ib = new InstructionBlock(cw, instructions, 0);
            bool success = BlockEncoder.TryEncode(x86 ? 32 : 64, ib, out string errMsg);
            if (!success)
                throw new Exception("Error during Iced encode: " + errMsg);
            byte[] bytes = cw.ToArray();

            var ptrStub = alloc(bytes.Length, 0x40);    // RWX
            writeBytes(ptrStub, bytes);

            return ptrStub;

            IntPtr alloc(int size, int protection = 0x04) => Native.VirtualAllocEx(hProc, IntPtr.Zero, (uint)size, 0x1000, protection);
            void writeBytes(IntPtr address, byte[] b) => Native.WriteProcessMemory(hProc, address, b, (uint)b.Length, out _);
            void writeString(IntPtr address, string str) => writeBytes(address, new UnicodeEncoding().GetBytes(str));

            IntPtr allocString(string? str)
            {
                if (str is null) return IntPtr.Zero;

                IntPtr pString = alloc(str.Length * 2 + 2);
                writeString(pString, str);
                return pString;
            }

            IntPtr allocBytes(byte[] buffer)
            {
                IntPtr pBuffer = alloc(buffer.Length);
                writeBytes(pBuffer, buffer);
                return pBuffer;
            }
        }

        sealed class CodeWriterImpl : CodeWriter {
            readonly List<byte> allBytes = new List<byte>();
            public override void WriteByte(byte value) => allBytes.Add(value);
            public byte[] ToArray() => allBytes.ToArray();
        }
    }
}
