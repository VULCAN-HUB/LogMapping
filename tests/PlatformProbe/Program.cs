using System.IO;
using Microsoft.Web.WebView2.Core;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Security.Principal;
var version=CoreWebView2Environment.GetAvailableBrowserVersionString();
Console.WriteLine("WebView2 Runtime: "+version);
if(string.IsNullOrWhiteSpace(version))return 1;
Console.WriteLine("64-bit process: "+Environment.Is64BitProcess);
Console.WriteLine("Administrator token: "+new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
var asm=Assembly.LoadFrom(Path.GetFullPath(args[0]));
using var html=asm.GetManifestResourceStream("LogMapping.resources.index.html")!;
var actual=Convert.ToHexString(SHA256.HashData(html));var expected=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1])));
Console.WriteLine("Embedded HTML matches source: "+(actual==expected));if(actual!=expected)return 1;
var resourceRoot=Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
foreach(var file in new[] { "about.js", "about.css", "app-icon.ico", "fonts/rajdhani-latin-700-normal.woff2", "fonts/Pretendard-Regular.woff2", "fonts/Pretendard-Bold.woff2", "fonts/Rajdhani-OFL.txt", "fonts/Pretendard-OFL.txt" })
{
    using var embedded=asm.GetManifestResourceStream("LogMapping.resources."+file.Replace('/', '.'));
    if(embedded==null || !SHA256.HashData(embedded).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(resourceRoot,file)))))
    { Console.WriteLine("FAIL embedded asset: "+file);return 1; }
    Console.WriteLine("PASS embedded asset: "+file);
}
Console.WriteLine("Runtime build version: "+asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
using var resources=new ResourceReader(asm.GetManifestResourceStream("LogMapping.g.resources")!);
var names=resources.Cast<System.Collections.DictionaryEntry>().Select(e=>(string)e.Key).ToArray();
Console.WriteLine("WPF resources: "+string.Join(", ",names));
if(!names.Contains("mainwindow.baml")||!names.Contains("icon.ico"))return 1;
Console.WriteLine("PASS runtime and packaged WPF resource probe (no desktop UI interaction)");return 0;
