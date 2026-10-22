# External capture dependency license provenance

Retrieved 2026-10-19 from the package nuspec repository commits or exact license URL. These files are unmodified source downloads; hash values are SHA256 of their bytes.

| File | Packages | Source | SHA256 |
| --- | --- | --- | --- |
| Vortice.Windows-LICENSE.txt | Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX 3.6.2 | https://raw.githubusercontent.com/amerkoleci/Vortice.Windows/cd916a03f206165bec67982ed501e88820d4182b/LICENSE | 82BD4E84D144BEBBB519ED05899F0CEAEAF2E0F95160AC32FD8CC388710C76D0 |
| Vortice.Mathematics-LICENSE.txt | Vortice.Mathematics 1.9.2 | https://raw.githubusercontent.com/amerkoleci/Vortice.Mathematics/f712049b1c3cd1671240af6a8ae22a635c4c34dd/LICENSE | CF72351FA2E9F2EB181AF7FD508DD73DA1D84BA43575CB98F7AF22F26A81AF5E |
| SharpGenTools-LICENSE.txt | SharpGen.Runtime, SharpGen.Runtime.COM 2.2.0-beta | https://raw.githubusercontent.com/SharpGenTools/SharpGenTools/a22348d2e1ff76dfbdc51d68800ed31e991d8b32/LICENSE.txt | 1CC7ABD1E97575838535484BEF6E4AB14E81681DDF7837672ACEDE5FCA1D4E30 |
| Windows-SDK-LICENSE.rtf | Microsoft.Windows.SDK.NET.Ref 10.0.19041.56, including supplied Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll | https://aka.ms/WinSDKLicenseURL redirects to https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf | DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62 |

Preserved additional SharpGen package attribution from both exact nuspecs: `(c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci`. This supplements the source LICENSE's earlier copyright line rather than silently replacing it.

SDK nuspec copyright: `© Microsoft Corporation. All rights reserved.` The SDK RTF identifies EULAID `WIN10SDK.RTM.AUG_2018_en-US`. Its complete text was read through in-memory RTF decoding. Section 2 references REDIST.TXT and https://go.microsoft.com/fwlink/?LinkId=524842, which redirects to https://learn.microsoft.com/en-us/legal/windows-sdk/redist. The full downloaded Markdown is preserved as `Windows-SDK-REDIST.md`, SHA256 `FF4DC82936EF9E8449927A3D06EE1FCA801933C1464CEEE60FA14003EB656DD0`; its metadata identifies source commit `5517c932e1189e1d731dc4a24ae8f6b2fdf38bd4`, updated 2024-10-21. Its Microsoft.Windows.SDK.NET.Ref section explicitly lists the package's net8.0 Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll for unmodified distribution as part of a program calling WinRT APIs, subject to the SDK terms. These exact shipped DLL bytes are preserved and independently bound to the final bundle.

Section 1's https://aka.ms/thirdpartynotices redirects to Microsoft's general third-party notice index. It was inspected on 2026-10-19; no SDK.NET.Ref-specific notice was identified there, and the exact restored package contains no separate physical notice file. This records the supplied package terms and discovered notices, rather than claiming a general legal review of all Microsoft software. WinRT.Runtime is not relabeled as a separately MIT-licensed download. The local candidate still requires runtime acceptance and is not approved for delivery.
