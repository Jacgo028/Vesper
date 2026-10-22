---
layout: ContentPage
title: Windows SDK redist lists | Microsoft Learn
canonicalUrl: https://learn.microsoft.com/en-us/legal/windows-sdk/redist
breadcrumb_path: /legal/bread/toc.json
ms.topic: legal
uhfHeaderId: MSDocsHeader-Windows
description: These are the redist.txt lists referenced in the EULA for the Windows SDK
keywords: redist, sdk
author: QuinnRadich
ms.author: quradic
ms.date: 2024-10-21T00:00:00.0000000Z
ms.service: legal
ms.subservice: uwp
ROBOTS: NOINDEX
locale: en-us
document_id: d62eba6c-ae20-0bde-9096-c86a61c96a0b
document_version_independent_id: 2ac21393-8a8b-012a-8add-3cebe8528ec8
updated_at: 2024-10-30T22:28:00.0000000Z
original_content_git_url: https://github.com/MicrosoftDocs/DocsLegal-pr/blob/live/DocsLegal/windows-sdk/redist.md
gitcommit: https://github.com/MicrosoftDocs/DocsLegal-pr/blob/5517c932e1189e1d731dc4a24ae8f6b2fdf38bd4/DocsLegal/windows-sdk/redist.md
git_commit_id: 5517c932e1189e1d731dc4a24ae8f6b2fdf38bd4
site_name: Docs
depot_name: MSDN.DocsLegal
page_type: content
toc_rel: ../toc.json
feedback_system: None
feedback_product_url: ''
feedback_help_link_type: ''
feedback_help_link_url: ''
word_count: 966
asset_id: windows-sdk/redist
moniker_range_name: 
monikers: []
item_type: Content
source_path: DocsLegal/windows-sdk/redist.md
platformId: 266b8e3d-14a8-60ca-3b58-05fa41fb434d
---

# Windows SDK redist lists | Microsoft Learn

UPDATED 10/21/2024

Below are separate “REDIST.TXT lists” referenced in the “Distributable Code” section of the Microsoft Software License Terms for the Microsoft Windows Software Development Kit for Windows 10 indicated below.

If you have a validly licensed copy of such software, you may copy and distribute the unmodified object code form of the files listed below, subject to the software's License Terms and to the additional terms or conditions that are indicated below.

### Static LIB files

Subject to the license terms for the software, the .lib files under the following directories may be distributed unmodified when built as part of your program:

- Program Files\Windows Kits\10\Lib\&lt;version&gt;\um\x86
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\um\x64
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\um\arm
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\um\arm64
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\ucrt\x86
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\ucrt\x64
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\ucrt\arm
- Program Files\Windows Kits\10\Lib\&lt;version&gt;\ucrt\arm64

Where &lt;version&gt; is the publically released version of the SDK, for example: 10.0.10586.0 and 10.0.10240.0.

### Binaries

Subject to the license terms for the software, the following .exe & .dll files may be distributed unmodified as part of your program (note that there are additional restrictions for some of the files below):

- Program Files\Windows Kits\10\Windows Performance Toolkit\KernelTraceControl.dll
- Program Files \Windows Kits\10\Windows Performance Toolkit\WindowsPerformanceRecorderControl.dll

The following files may be distributed with your Universal Windows app or Classic Windows applications:

- Program Files\Windows Kits\10\Redist\MBN\x64\microsoft.mbn.dll
- Program Files\Windows Kits\10\Redist\MBN\x86\microsoft.mbn.dll
- Program Files\Windows Kits\10\Redist\MBN\arm\microsoft.mbn.dll
- Program Files\Windows Kits\10\Redist\MBN\arm64\microsoft.mbn.dll

These files may only be redistributed with your Classic Windows applications. They may not be redistributed with any Universal Windows apps.

- Program Files\Windows Kits\10\Redist\D3D\x64\d3dcompiler\_47.dll
- Program Files\Windows Kits\10\Redist\D3D\x64\d3dcsx\_47.dll
- Program Files\Windows Kits\10\Redist\D3D\x64\dxcompiler.dll
- Program Files\Windows Kits\10\Redist\D3D\x64\dxil.dll
- Program Files\Windows Kits\10\Redist\D3D\x86\d3dcompiler\_47.dll
- Program Files\Windows Kits\10\Redist\D3D\x86\d3dcsx\_47.dll
- Program Files\Windows Kits\10\Redist\D3D\x86\dxcompiler.dll
- Program Files\Windows Kits\10\Redist\D3D\x86\dxil.dll

These files may only be redistributed with your Classic Windows applications. They may not be redistributed with any Universal Windows apps.

- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-console-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-datetime-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-debug-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-errorhandling-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-file-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-file-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-file-l2-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-handle-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-heap-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-interlocked-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-libraryloader-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-localization-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-memory-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-namedpipe-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-processenvironment-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-processthreads-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-processthreads-l1-1-1.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-profile-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-rtlsupport-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-string-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-synch-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-synch-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-sysinfo-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-timezone-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-core-util-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-conio-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-convert-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-environment-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-filesystem-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-heap-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-locale-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-math-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-multibyte-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-private-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-process-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-runtime-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-stdio-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-string-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-time-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\api-ms-win-crt-utility-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\arm\ucrtbase.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-console-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-datetime-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-debug-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-errorhandling-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-file-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-file-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-file-l2-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-handle-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-heap-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-interlocked-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-libraryloader-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-localization-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-memory-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-namedpipe-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-processenvironment-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-processthreads-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-processthreads-l1-1-1.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-profile-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-rtlsupport-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-string-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-synch-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-synch-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-sysinfo-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-timezone-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-core-util-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-conio-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-convert-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-environment-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-filesystem-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-heap-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-locale-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-math-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-multibyte-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-private-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-process-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-runtime-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-stdio-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-string-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-time-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\api-ms-win-crt-utility-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x64\ucrtbase.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-console-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-datetime-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-debug-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-errorhandling-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-file-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-file-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-file-l2-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-handle-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-heap-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-interlocked-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-libraryloader-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-localization-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-memory-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-namedpipe-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-processenvironment-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-processthreads-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-processthreads-l1-1-1.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-profile-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-rtlsupport-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-string-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-synch-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-synch-l1-2-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-sysinfo-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-timezone-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-core-util-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-conio-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-convert-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-environment-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-filesystem-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-heap-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-locale-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-math-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-multibyte-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-private-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-process-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-runtime-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-stdio-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-string-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-time-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\api-ms-win-crt-utility-l1-1-0.dll
- Program Files\Windows Kits\10\Redist\ucrt\DLLs\x86\ucrtbase.dll

### Universal CRT MSU files

The Universal CRT MSU files can be downloaded from the following location: https://go.microsoft.com/fwlink/?LinkId=718052

These files may only be redistributed unmodified with your setup program for your Classic Windows applications. They may not be redistributed with any Universal Windows apps.

- Windows8.1-KB3118401-x64.msu
- Windows8.1-KB3118401-x86.msu
- Windows8.1-KB3118401-arm.msu
- Windows8-RT-KB3118401-x64.msu
- Windows8-RT-KB3118401-x86.msu
- Windows8-RT-KB3118401-arm.msu
- Windows6.1-KB3118401-x64.msu
- Windows6.1-KB3118401-x86.msu
- Windows6.0-KB3118401-x64.msu
- Windows6.0-KB3118401-x86.msu

If you need an earlier version of these files, they can be located here: https://support.microsoft.com/kb/2999226

### Windows Performance Toolkit

The following files may only be distributed unmodified, as a package, as part of your program:

- Program Files\Windows Kits\10\Windows Performance Toolkit\Redistributables\WPTarm-arm\_en-us.msi
- Program Files\Windows Kits\10\Windows Performance Toolkit\Redistributables\WPTx64-x86\_en-us.msi
- Program Files\Windows Kits\10\Windows Performance Toolkit\Redistributables\WPTx86-x86\_en-us.msi

### Debugging Tools for Windows

You may distribute these files as part of your program.

- Dbgeng.dll
- Dbgcore.dll
- Dbgmodel.dll
- Dbghelp.dll
- Srcsrv.dll
- Symsrv.dll
- Symchk.exe
- Symbolcheck.dll
- Symstore.exe
- Program Files\Windows Kits\10\Debuggers\Redist\X86 Debuggers and Tools-x86\_en-us.msi
- Program Files\Windows Kits\10\Debuggers\Redist\X64 Debuggers and Tools-x64\_en-us.msi
- App Verifier

The following files may only be distributed unmodified, as a package, as part of your program:

- Program Files\Application Verifier\vrfauto.dll
- Program Files\Application Verifier\vrfauto.h
- Program Files\Application Verifier\vrfauto.idl

In order to acquire the following MSIs, you must download the software and select the appropriate features from the feature selection screen. The MSIs can be found in the Installers folder in the location where you downloaded the software.

- Application Verifier x64 External Package-x64\_en-us.msi
- Application Verifier x86 External Package-x86\_en-us.msi

### Windows Packaging Tools

The following files may only be distributed unmodified, as a package, and as part of your program for the use of packing, unpacking, bundling, and unbundling of msix and msixbundle files.

- Appxpackaging.dll
- AppxPackaging.dll.mui
- AppxSip.dll
- ComparePackage.exe
- MakeAppx.exe
- MakePri.exe
- Microsoft.ComparePackage.Lib.dll
- Microsoft.PackageEditor.Lib.dll
- Microsoft.Packaging.SDKUtils.dll
- Microsoft.Windows.Build.Appx.AppxPackaging.dll.manifest
- Microsoft.Windows.Build.Appx.AppxSip.dll.manifest
- Microsoft.Windows.Build.Appx.OpcServices.dll.manifest
- Microsoft.Windows.Build.Signing.mssign32.dll.manifest
- Microsoft.Windows.Build.Signing.wintrust.dll.manifest
- msisip.dll
- Mssign32.dll
- OpcServices.dll
- PackageEditor.exe
- SignTool.exe
- SignTool.exe.manifest
- Wintrust.dll
- Wintrust.dll.ini

### Microsoft.Windows.SDK.NET.Ref

The files included in the Microsoft.Windows.SDK.NET.Ref Nuget package (https://www.nuget.org/packages/Microsoft.windows.sdk.net.ref) may be distributed unmodified as a NuGet package, or as part of your program in order to enable your application to call WinRT Apis ([https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance](/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance)).

- ./analyzers/dotnet/cs/WinRT.SourceGenerator.dll
- ./data/frameworkList.xml
- ./data/RuntimeList.xml
- ./lib/net8.0/Microsoft.Windows.SDK.NET.dll
- ./lib/net8.0/Microsoft.Windows.SDK.NET.xml
- ./lib/net8.0/Microsoft.Windows.UI.Xaml.dll
- ./lib/net8.0/Microsoft.Windows.UI.Xaml.xml
- ./lib/net8.0//WinRT.Runtime.dll
- ./winmd/\*.winmd
- ./lib/net6.0/Microsoft.Windows.SDK.NET.dll
- ./lib/net6.0/Microsoft.Windows.SDK.NET.xml
- ./lib/net6.0/WinRT.Runtime.dll