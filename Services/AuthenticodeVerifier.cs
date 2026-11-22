using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CryoTray.Services
{
    public static class AuthenticodeVerifier
    {
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const int TrustENoSignature = unchecked((int)0x800B0100);
        private const int TrustEBadDigest = unchecked((int)0x80096010);

        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static void VerifyOrAllowUnsigned(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(LocalizationService.Get("Update_ExeMissing"), filePath);
            }

            int result = Verify(filePath);
            if (result == TrustENoSignature)
            {
                return;
            }

            if (result == TrustEBadDigest)
            {
                throw new InvalidOperationException(LocalizationService.Get("Update_SignatureInvalid"));
            }

            if (result != 0)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_SignatureInvalid") +
                    $" (0x{result:X8})");
            }
        }

        private static int Verify(string filePath)
        {
            IntPtr filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
            IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            IntPtr fileDataPointer = IntPtr.Zero;

            try
            {
                var fileInfo = new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = filePathPointer,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                fileDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
                var trustData = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPointer,
                    dwStateAction = WtdStateActionVerify,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = 0,
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero
                };
                Marshal.StructureToPtr(trustData, fileDataPointer, false);

                Guid actionId = WinTrustActionGenericVerifyV2;
                int result = WinVerifyTrust(IntPtr.Zero, ref actionId, fileDataPointer);

                trustData = Marshal.PtrToStructure<WinTrustData>(fileDataPointer);
                trustData.dwStateAction = WtdStateActionClose;
                Marshal.StructureToPtr(trustData, fileDataPointer, false);
                WinVerifyTrust(IntPtr.Zero, ref actionId, fileDataPointer);

                return result;
            }
            finally
            {
                if (fileDataPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(fileDataPointer);
                }

                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeCoTaskMem(filePathPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            [In] ref Guid actionId,
            IntPtr trustData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
