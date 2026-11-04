/* Copyright (c) FufuLauncher Dev Team. Licensed under the MIT License. */
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Downloader;
using FufuLauncher.Updates;

namespace Updater;
public partial class MainWindow
{
    private bool VerifyFileSha256(string filePath)
    {
        if (string.IsNullOrEmpty(_expectedSha256))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] hashBytes = SHA256.HashData(stream);
            StringBuilder sb = new();
            foreach (var b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }

            string actualHash = sb.ToString();
            return string.Equals(actualHash, _expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
