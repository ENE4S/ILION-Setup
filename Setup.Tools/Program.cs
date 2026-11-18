using System.Security.Cryptography;
using Ilion.Setup;

// claves <privada.pem> <publica.txt> : crea el par ECDSA P-256 (no sobrescribe una clave existente)
// firmar <manifiesto.json> <privada.pem> : escribe <manifiesto.json>.firma
switch (args.ElementAtOrDefault(0))
{
    case "claves":
        if (File.Exists(args[1])) { Console.WriteLine("Ya existe una clave privada: no se sobrescribe."); return 1; }
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(args[1], key.ExportECPrivateKeyPem());
            File.WriteAllText(args[2], Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        }
        Console.WriteLine("Clave creada."); return 0;
    case "firmar":
        File.WriteAllText(args[1] + ".firma", Manifests.Sign(File.ReadAllBytes(args[1]), File.ReadAllText(args[2])));
        Console.WriteLine("Firmado: " + args[1] + ".firma"); return 0;
    default:
        Console.WriteLine("Uso: claves <privada.pem> <publica.txt> | firmar <manifiesto.json> <privada.pem>"); return 2;
}
