# Genera el manifiesto de la base congelada de ILION Setup a partir de los archivos oficiales ya verificados
import hashlib, json, os, sys
# Uso: python generar-base.py <salida.json> <carpeta-extraida-del-nightly> <paquete-ILION-de-referencia>
OUT, S, PKG = sys.argv[1], sys.argv[2], sys.argv[3]
sha = lambda p: hashlib.sha256(open(p, 'rb').read()).hexdigest()

nightly = {
    "id": "optiscaler-nightly-20261009", "tipo": "archivo-7z",
    "url": "https://github.com/optiscaler/OptiScaler-nightly/releases/download/nightly-20261009/OptiScaler_v10.0.0-pre1_20261009.7z",
    "sha256": sha(os.path.join(S, 'dl', 'nightly.7z')),
    "nota": "Compilación oficial de OptiScaler en el commit f6f75ddf (último cambio de esa nightly; la del 10/10 no tuvo cambios). Es la base del código de ILION (commit de importación 0986dc8).",
}
streamline = {
    "id": "streamline-sdk-2.14.1", "tipo": "archivo-zip",
    "url": "https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.1/streamline-sdk-v2.14.1.zip",
    "sha256": "92c4d954631a1710da86ca3fa8d5034f2b9503838c95fc4ae977ae149319781b",
    "nota": "SDK oficial de NVIDIA (lo que descarga Streamlined_fetcher_windows.bat de OptiScaler). Verificado archivo a archivo por CRC32 del índice y SHA-256 del paquete de referencia.",
}
ffx = "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/v2.4.0/Kits/FidelityFX/signedbin/"

files = []
def add(dest, origin, entry, local, firma=None, opcional=False):
    files.append({"destino": dest, "origen": origin, "entrada": entry, "sha256": sha(local), "tamano": os.path.getsize(local),
                  **({"firmante": firma} if firma else {}), **({"opcional": True} if opcional else {})})

x = os.path.join(S, 'x')
for name, firma in [("libxess.dll", "Intel Corporation"), ("libxess_dx11.dll", "Intel Corporation"), ("libxess_fg.dll", "Intel Corporation"),
                    ("libxell.dll", "Intel Corporation"), ("amd_fidelityfx_vk.dll", "Advanced Micro Devices"),
                    ("dlssg_to_fsr3_amd_is_better.dll", None), ("Streamlined_fetcher_windows.bat", None)]:
    add("OptiScaler\\" + name, nightly["id"], "OptiScaler/" + name, os.path.join(x, 'OptiScaler', name), firma)
for lic in ["XeSS_LICENSE.txt", "FidelityFX_v2_LICENSE.md", "DirectX_LICENSE.txt"]:
    add("Licenses\\" + lic, nightly["id"], "Licenses/" + lic, os.path.join(x, 'Licenses', lic))
add("OptiScaler.ini", nightly["id"], "OptiScaler.ini", os.path.join(x, 'OptiScaler.ini'), opcional=True)
add("OptiScaler\\D3D12_OptiScaler\\D3D12Core.dll", nightly["id"], "OptiScaler/D3D12_OptiScaler/D3D12Core.dll",
    os.path.join(x, 'OptiScaler', 'D3D12_OptiScaler', 'D3D12Core.dll'), "Microsoft Corporation", opcional=True)
for name in ["amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll"]:
    files.append({"destino": "OptiScaler\\" + name, "origen": "url", "url": ffx + name, "sha256": sha(os.path.join(PKG, 'OptiScaler', name)),
                  "tamano": os.path.getsize(os.path.join(PKG, 'OptiScaler', name)), "firmante": "Advanced Micro Devices"})
sl = os.path.join(PKG, 'OptiScaler', 'streamline')
for name in sorted(os.listdir(sl)):
    entry = "_artifacts/sl.dlss_g/Production_x64/sl.dlss_g.dll" if name == "sl.dlss_g.dll" else "bin/x64/" + name
    add("OptiScaler\\streamline\\" + name, streamline["id"], entry, os.path.join(sl, name), "NVIDIA Corporation")

manifest = {
    "formato": 1,
    "nombre": "Base congelada de OptiScaler para ILION 1.4.1.2",
    "fecha": "2026-11-17",
    "motor": {"nombre": "OptiScaler ILION", "version": "1.4.1.2 beta", "destino": "dxgi.dll",
              "sha256": sha(os.path.join(PKG, 'dxgi.dll')), "codigo": "rama ilion-1.4.1.2-beta-correcciones, commit b441a1d",
              "base": "OptiScaler f6f75ddf0118ac45f447e2babbe18dbda3451edd + cambios de ILION",
              "nota": "No hay fuente oficial publicada configurada: se instala desde un paquete o DLL local verificada por su huella."},
    "paquetes": [nightly, streamline],
    "archivos": files,
    "justificacion": [
        "El commit de importación de ILION (0986dc8) tiene los mismos blobs que OptiScaler f6f75ddf (Config.h, dllmain.cpp, Nvngx_FG.cpp, FSR31Feature.cpp comprobados con la API de GitHub).",
        "nightly-20261009 es la compilación oficial de f6f75ddf: XeSS 2.0.2, XeFG 1.3.1.78, XeLL 1.3.2, amd_fidelityfx_vk 1.0.1, Nukem 0.130 y el fetcher son idénticos byte a byte al paquete de referencia del usuario.",
        "FidelityFX DX12 (loader 2.3.0, upscaler 4.1.2, FG 4.0.2, build 4555): el paquete de referencia los subió desde FSR 4.1.1; son idénticos a FidelityFX-SDK v2.4.0 signedbin (blob de Git) y a los de OptiScaler 0.9.5 oficial.",
        "streamline\\: 16 archivos idénticos a Streamline SDK v2.14.1 (CRC32 y tamaño del índice oficial).",
        "amd_fidelityfx_vk.dll se mantiene en 1.0.1: la implementación Vulkan de ILION elige el FSR 4 del driver por su posición en la lista de esa DLL.",
        "OptiScaler.ini oficial de f6f75ddf: solo cuando se instala una DLL suelta sin el paquete de ILION (ILION completa sus claves al guardar).",
        "Las licencias (Licenses\\) van como documentación de Setup; el instalador de OptiScaler no copia .txt ni .md al juego.",
        "D3D12Core.dll (Agility SDK) viene en la compilación oficial pero no en el paquete probado: opcional, solo para [FSR] FsrAgilitySDKUpgrade.",
    ],
}
os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, 'w', encoding='utf-8').write(json.dumps(manifest, ensure_ascii=False, indent=1) + '\n')
print(len(files), 'archivos')
