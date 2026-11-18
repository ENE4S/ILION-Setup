# Añade al manifiesto de la base el motor ILION publicado y los componentes que se instalan con él.
# Uso: python -I agregar-ilion.py base-congelada.json   (idempotente: reemplaza lo que ya hubiera de estos grupos)
import json, sys

path = sys.argv[1]
m = json.load(open(path, encoding="utf-8"))
REL = "https://github.com/ENE4S/Optiscaler-ILION/releases/download"

paquetes = [
    {"id": "ilion-v1.4.1.2-beta", "tipo": "archivo-zip",
     "url": f"{REL}/ilion-v1.4.1.2-beta/OptiScaler-ILION-1.4.1.2-beta.zip",
     "sha256": "e81a3d1ecc861cbf1d1b5485cf34a1448a18793523e6dc7823fcdb2d1eedcf6a",
     "nota": "Publicación oficial de ILION 1.4.1.2 beta (ENE4S/Optiscaler-ILION): dxgi.dll y OptiScaler.ini. Código: rama ilion-1.4.1.2-beta (b441a1d), GPL-3.0."},
    {"id": "ilion-variantes-1", "tipo": "archivo-zip",
     "url": f"{REL}/ilion-componentes-1/ILION-variantes-1.zip",
     "sha256": "0b6f615d61bbbcac1d895ae8a4bc61a638eb8dd2a7a2d49d6f60fe52796fd5e0",
     "nota": "Componentes sin otra fuente pública: FSR 4.1.1B de la comunidad de OptiScaler y variantes XeMFG Clásica y WMMA."},
    {"id": "ilion-lmxxf-nr-runtime-1", "tipo": "archivo-zip",
     "url": f"{REL}/ilion-componentes-1/ILION-lmxxf-nr-runtime-1.zip",
     "sha256": "83da70366eda89d70ab7b8d407dca5333041937c6931c3d4bd70567c7819a400",
     "nota": "Runtime NR compilado por ILION desde lmxxf/dlss5-on-amd-9070xt-porting (MIT). Solo AMD RDNA 3 / RDNA 4."},
    {"id": "ilion-extras-1", "tipo": "archivo-zip",
     "url": f"{REL}/ilion-componentes-1/ILION-extras-1.zip",
     "sha256": "9b240643d5d6ab466b66ec321cc7fc5a9d182e3ea93647cbe93e20e7955fe7c2",
     "nota": "Instalar_ReShade.bat de ILION (ReShade 6.8.0 oficial) y DLSS Enabler 4.10.0.18 de Artur (MIT)."},
    {"id": "fsr4-overrides-2026-10-22", "tipo": "archivo-7z",
     "url": "https://github.com/zgauthier2000/fsr4-shader-performance-overrides/releases/download/dll-2026-10-22/fsr4.1.1-cyboman-rx7000-2026-10-22.7z",
     "sha256": "e6e507288c003d8063c3c12a70cb94b692499d894a9c2703c816450176155c43",
     "nota": "FSR 4.1.1 Exact, Lossy y Lossy2 de zgauthier2000 (GPL-2.0), publicación dll-2026-10-22: idénticas a las del paquete de ILION."},
]
FSR = r"ILION\Escaladores\FSR4"
archivos = [
    # Motor
    {"destino": "dxgi.dll", "origen": "ilion-v1.4.1.2-beta", "entrada": "dxgi.dll", "sha256": m["motor"]["sha256"], "tamano": 29869056, "firmante": None, "grupo": "motor"},
    {"destino": "OptiScaler.ini", "origen": "ilion-v1.4.1.2-beta", "entrada": "OptiScaler.ini", "sha256": "e84c4da9daf2cd7c7957aedc5126b434b8069339c5e4f908a52739b5a6b329aa", "tamano": 59299, "firmante": None, "grupo": "motor"},
    # Variantes de FSR 4 (se eligen en el menú de ILION)
    {"destino": FSR + r"\Oficial\amd_fidelityfx_upscaler_dx12.dll", "origen": "url",
     "url": "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/v2.3.0/Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll",
     "sha256": "d0dcccc74a43c44ba435b7a369b456e0970d8a4464e4bd683119b374f2c9fb46", "tamano": 28761864, "firmante": "Advanced Micro Devices", "grupo": "variantes"},
    {"destino": FSR + r"\Exact\amd_fidelityfx_upscaler_dx12.dll", "origen": "fsr4-overrides-2026-10-22", "entrada": "exact/amd_fidelityfx_upscaler_dx12.dll",
     "sha256": "6e42d8b6b67d28fafeebf2e495c1b51b85389bcdf35a5a0f1e760b9a3760abd5", "tamano": 38354944, "firmante": None, "grupo": "variantes"},
    {"destino": FSR + r"\Lossy\amd_fidelityfx_upscaler_dx12.dll", "origen": "fsr4-overrides-2026-10-22", "entrada": "lossy/amd_fidelityfx_upscaler_dx12.dll",
     "sha256": "1f4d68b8875ad2f1631eb2232d755b9282a6eb80787891e74d77d1573d42b302", "tamano": 51553792, "firmante": None, "grupo": "variantes"},
    {"destino": FSR + r"\Lossy2\amd_fidelityfx_upscaler_dx12.dll", "origen": "fsr4-overrides-2026-10-22", "entrada": "lossy2/amd_fidelityfx_upscaler_dx12.dll",
     "sha256": "d3388bdd60c5a04c18019d47bcb7eb73e4a8a1ab2bb5b9fca9ff0508ae4317ce", "tamano": 50586112, "firmante": None, "grupo": "variantes"},
    {"destino": FSR + r"\Community_4.1.1B\amd_fidelityfx_upscaler_dx12.dll", "origen": "ilion-variantes-1", "entrada": "ILION/Escaladores/FSR4/Community_4.1.1B/amd_fidelityfx_upscaler_dx12.dll",
     "sha256": "0dd77d9c78d1ef9bc330cf4697ab3ffe24bc1aa7850e4130263dc922107fbd75", "tamano": 34013696, "firmante": None, "grupo": "variantes"},
    # Variantes XeMFG y OptiPatcher (plugins)
    {"destino": r"OptiScaler\plugins\XeMFG\Clasica\XeFGUnlock.asi", "origen": "ilion-variantes-1", "entrada": "OptiScaler/plugins/XeMFG/Clasica/XeFGUnlock.asi",
     "sha256": "614c517fe3b498b2262236f2472325c768975526dbcdbe9bf04f1f7161686e50", "tamano": 38912, "firmante": None, "grupo": "variantes"},
    {"destino": r"OptiScaler\plugins\XeMFG\WMMA\MFGUnlocker.asi", "origen": "ilion-variantes-1", "entrada": "OptiScaler/plugins/XeMFG/WMMA/MFGUnlocker.asi",
     "sha256": "23774892052f4575c0b4c8481ccebf81c2f3c85d3bbba18471e095b89748f04f", "tamano": 12125696, "firmante": None, "grupo": "variantes"},
    {"destino": r"OptiScaler\plugins\OptiPatcher.asi", "origen": "url", "url": "https://github.com/optiscaler/OptiPatcher/releases/download/v0.42/OptiPatcher_v0.42.asi",
     "sha256": "2a6e033aeda72d63281889480b2380da830530cf6e1e448e2056d451ade75f1b", "tamano": 107520, "firmante": None, "grupo": "variantes"},
    # Asistente de ReShade de ILION, junto al ejecutable
    {"destino": "Instalar_ReShade.bat", "origen": "ilion-extras-1", "entrada": "Instalar_ReShade.bat",
     "sha256": "8ccd16a4688cf257355356eb1817f92b13e6d49c81da7b742157f85c505db2d7", "tamano": 39213, "firmante": None, "grupo": "variantes"},
    # Runtime NR de lmxxf (RDNA 3 / RDNA 4)
    {"destino": "LmxxfNrRuntime.dll", "origen": "ilion-lmxxf-nr-runtime-1", "entrada": "LmxxfNrRuntime.dll",
     "sha256": "1e6dce7ef7226f722ad7990315e72c794420990a932ae782661a38cabf4c6c4c", "tamano": 1530880, "firmante": None, "grupo": "lmxxf"},
    {"destino": "LmxxfNrRuntime.pak", "origen": "ilion-lmxxf-nr-runtime-1", "entrada": "LmxxfNrRuntime.pak",
     "sha256": "9d92106e6e6e5204a2e2c181847ba74bd49c7bf54e88466946fb2fee19cbb542", "tamano": 759906475, "firmante": None, "grupo": "lmxxf"},
]
grupos = {"motor", "variantes", "lmxxf"}
ids = {p["id"] for p in paquetes}
m["paquetes"] = [p for p in m["paquetes"] if p["id"] not in ids] + paquetes
m["archivos"] = [f for f in m["archivos"] if f.get("grupo") not in grupos] + archivos
m["motor"]["nota"] = "Fuente oficial: publicación ilion-v1.4.1.2-beta de ENE4S/Optiscaler-ILION (grupo «motor»). Un paquete o DLL local sigue siendo opción avanzada."
m["fecha"] = "2026-11-20"
ev = "Variantes: FSR 4.1.1 oficial = FidelityFX SDK v2.3.0 (blob idéntico); Exact/Lossy/Lossy2 = zgauthier2000 dll-2026-10-22 (idénticas); 4.1.1B, XeMFG Clásica/WMMA y runtime de lmxxf en ilion-componentes-1."
m["justificacion"] = [j for j in m["justificacion"] if not j.startswith("Variantes:")] + [ev]
json.dump(m, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(len(m["archivos"]), "archivos,", len(m["paquetes"]), "paquetes")
