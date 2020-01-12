using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.Linux;
using static Tmds.Linux.LibC;

// rought adaptation of https://github.com/lizrice/containers-from-scratch/blob/master/main.go

namespace container
{
    class Program
    {
        private string[] childArgs;
        unsafe private delegate int execChild(void *unused);

        static void Main(string[] args)
        {
            var program = new Program(args);

            if (args.Length < 1) {
                Console.WriteLine("Usage: container run <cmd> <arg>...");
                return;
            }
            switch (args[0]) {
                case "run":
                    program.Run();
                    break;
                case "child":
                    program.Child();
                    break;
                default:
                    throw new ArgumentException(String.Format("invalid command {0}", args[0]));
            }
        }

        Program(string[] args)
        {
            childArgs = args;
        }

        private void Child()
        {
            Console.WriteLine("child");
        }

        private unsafe void Run()
        {
            int stackSize = 1024 * 1024;
            byte* stackBuf = stackalloc byte[stackSize]; // TODO: make a span?
            int ret = clone(
                (void *)Marshal.GetFunctionPointerForDelegate<execChild>(ExecChild),
                (void *)(stackBuf+stackSize), // stack grows donwards
                CLONE_NEWNS|CLONE_NEWUTS|CLONE_NEWPID,
                null);
            if (ret < 0)
            {
                PlatformException.Throw();
            }
        }

        private unsafe void SetHostname(string hostname)
        {
            int byteLength = Encoding.UTF8.GetByteCount(hostname) + 1;
            Span<byte> bytes = byteLength <= 128 ? stackalloc byte[byteLength] : new byte[byteLength];
            Encoding.UTF8.GetBytes(hostname, bytes);

            fixed (byte* host = bytes)
            {
                int ret = sethostname(host, byteLength);
                if (ret < 0)
                {
                    PlatformException.Throw();
                }
            }
        }

        private unsafe void ChrootHome()
        {
            string homePath = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            int byteLength = Encoding.UTF8.GetByteCount(homePath) + 1;
            Span<byte> bytes = byteLength <= 128 ? stackalloc byte[byteLength] : new byte[byteLength];
            Encoding.UTF8.GetBytes(homePath, bytes);

            fixed (byte* path = bytes)
            {
                int ret = chroot(path);
                if (ret < 0)
                {
                    PlatformException.Throw();
                }
            }
        }

        private unsafe int ExecChild(void *unused)
        {
            SetHostname("test-container");
            ChrootHome();
            Directory.SetCurrentDirectory("/");
            
            // mount proc
            int ret = unshare(CLONE_NEWNS);
            if (ret < 0)
            {
                PlatformException.Throw();
            }
            // TODO: execve
            //Console.WriteLine(String.Format("{0}", childArgs[1..]));
            return 0;
        }
       
        private void CGroup(string name)
        {
            var basepath = Path.Join("/sys/fs/cgroup/pids", name);
            Directory.CreateDirectory(basepath);
            File.WriteAllText(Path.Join(basepath, "pids.max"), "20");
            // Removes the new cgroup in place after the container exits
            File.WriteAllText(Path.Join(basepath, "notify_on_release"), "1");
            File.WriteAllText(Path.Join(basepath, "cgroup.procs"), String.Format("{0}", Process.GetCurrentProcess().Id));
        }
    }

    class PlatformException : Exception
    {
        public PlatformException(int errno) :
            base(GetErrorMessage(errno))
        {
            HResult = errno;
        }

        public PlatformException() :
            this(LibC.errno)
        {}

        private unsafe static string GetErrorMessage(int errno)
        {
            int bufferLength = 1024;
            byte* buffer = stackalloc byte[bufferLength];

            int rv = strerror_r(errno, buffer, bufferLength);

            return rv == 0 ? Marshal.PtrToStringAnsi((IntPtr)buffer) : $"errno {errno}";
        }

        public static void Throw() => throw new PlatformException();
    }
}
