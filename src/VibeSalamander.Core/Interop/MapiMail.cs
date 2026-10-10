using System.Runtime.InteropServices;

namespace VibeSalamander.Core.Interop;

/// <summary>Email (Ctrl+E): a new message with the files attached, in the default e-mail program (Simple MAPI).</summary>
public static class MapiMail
{
    public enum Result { Sent, Cancelled, NoMailProgram, Failed }

    public static Task<Result> SendFilesAsync(IntPtr owner, IReadOnlyList<string> files)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        // MAPI wants an STA thread and shows its own (modal) compose window
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Send(owner, files));
            }
            catch (Exception)
            {
                tcs.SetResult(Result.NoMailProgram); // mapi32.dll missing or broken
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static Result Send(IntPtr owner, IReadOnlyList<string> files)
    {
        int size = Marshal.SizeOf<MapiFileDescW>();
        IntPtr array = Marshal.AllocHGlobal(size * files.Count);
        var strings = new List<IntPtr>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                IntPtr path = Marshal.StringToHGlobalUni(files[i]);
                IntPtr name = Marshal.StringToHGlobalUni(Path.GetFileName(files[i]));
                strings.Add(path);
                strings.Add(name);
                var desc = new MapiFileDescW { nPosition = uint.MaxValue, lpszPathName = path, lpszFileName = name };
                Marshal.StructureToPtr(desc, array + i * size, false);
            }
            var message = new MapiMessageW { nFileCount = (uint)files.Count, lpFiles = array };
            uint rc = MAPISendMailW(IntPtr.Zero, owner, ref message, MAPI_LOGON_UI | MAPI_DIALOG, 0);
            return rc switch
            {
                0 => Result.Sent,
                MAPI_USER_ABORT => Result.Cancelled,
                MAPI_E_FAILURE or MAPI_E_LOGIN_FAILURE or MAPI_E_NOT_SUPPORTED => Result.NoMailProgram,
                _ => Result.Failed,
            };
        }
        finally
        {
            foreach (IntPtr s in strings)
                Marshal.FreeHGlobal(s);
            Marshal.FreeHGlobal(array);
        }
    }

    private const uint MAPI_LOGON_UI = 0x1, MAPI_DIALOG = 0x8;
    private const uint MAPI_USER_ABORT = 1, MAPI_E_FAILURE = 2, MAPI_E_LOGIN_FAILURE = 3, MAPI_E_NOT_SUPPORTED = 26;

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiFileDescW
    {
        public uint ulReserved;
        public uint flFlags;
        public uint nPosition;
        public IntPtr lpszPathName;
        public IntPtr lpszFileName;
        public IntPtr lpFileType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiMessageW
    {
        public uint ulReserved;
        public IntPtr lpszSubject;
        public IntPtr lpszNoteText;
        public IntPtr lpszMessageType;
        public IntPtr lpszDateReceived;
        public IntPtr lpszConversationID;
        public uint flFlags;
        public IntPtr lpOriginator;
        public uint nRecipCount;
        public IntPtr lpRecips;
        public uint nFileCount;
        public IntPtr lpFiles;
    }

    [DllImport("mapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MAPISendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);
}
