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
            Execve(childArgs[1], childArgs[2..]);
        }

        private unsafe void Run()
        {
            int stackSize = 1024 * 1024;
            Span<byte> stackBuf = stackalloc byte[stackSize];
            fixed (byte* stack = stackBuf)
            {
                int childPid = clone(
                    (void *)Marshal.GetFunctionPointerForDelegate<execChild>(ExecChild),
                    (void *)(stack+stackSize), // stack grows downwards
                    SIGCHLD /* |CLONE_NEWNS|CLONE_NEWUTS|CLONE_NEWPID */,
                    null);
                if (childPid < 0)
                {
                    PlatformException.Throw();
                }
                // wait for child to exit
                // TODO: this is somehow not working, maybe because of clone()
                Process.GetProcessById(childPid).WaitForExit();
            }
        }

        private unsafe void SetHostname(string hostname)
        {
            int byteLength = Encoding.UTF8.GetByteCount(hostname);
            Span<byte> bytes = stackalloc byte[byteLength];
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

        private unsafe void Chroot(string root)
        {
            fixed (byte* path = CString(root).Span)
            {
                int ret = chroot(path);
                if (ret < 0)
                {
                    PlatformException.Throw();
                }
            }
        }

        private unsafe void Mount()
        {
            // TODO
        }

        private unsafe int ExecChild(void *unused)
        {
            /*
            string homePath = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            
            SetHostname("test-container");
            Chroot(homePath);
            Directory.SetCurrentDirectory("/");
            */

            // TODO mount /proc

            /*
            int ret = unshare(CLONE_NEWNS);
            if (ret < 0)
            {
                PlatformException.Throw();
            }
            */

            childArgs[0] = "child";
            Execve("/proc/self/exe", childArgs);
            return 0;
        }

        private unsafe void Execve(string path, string[] args)
        {
            fixed (byte* _path = CString(path).Span)
            {
                byte*[] _args = new byte*[args.Length+2]; // leave the last element to NULL
                _args[0] = _path;
                for (var i = 0; i < args.Length; i++)
                {
                    fixed(byte* arg = CString(args[i]).Span)
                    {
                        _args[i+1] = arg;
                    }
                }
                byte*[] env = { null }; // TODO: inherit env?
                
                fixed (byte **argv = _args)
                fixed (byte **environ = env)
                {
                    if (execve(_path, argv, environ) < 0)
                    {
                        PlatformException.Throw();
                    }
                }
            }
        }
      
        private Memory<byte> CString(string str)
        {
            int byteLength = Encoding.UTF8.GetByteCount(str) + 1;
            byte[] bytes = new byte[byteLength];
            Encoding.UTF8.GetBytes(str, bytes);
            return new Memory<byte>(bytes);
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
