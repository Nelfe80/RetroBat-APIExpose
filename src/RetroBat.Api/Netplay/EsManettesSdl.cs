using System.Runtime.InteropServices;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Netplay;

/// <summary>
/// L'ENUMERATION DES MANETTES QU'EMULATIONSTATION FAIT A SON DEMARRAGE (InputManager::rebuildAllJoysticks, Windows),
/// refaite dans le processus enfant <see cref="EsManettes.Argument"/> : le SDL d'ES, ses reglages (HIDAPI selon
/// `HidJoysticks`, Wii et PS5 coupes, RawInput a son defaut), ses deux bases de mappages, le GUID sans ses octets de CRC,
/// la vue XInput en double ecartee, et le chemin parent Windows du peripherique.
///
/// Un processus a part : le lecteur de manettes de l'API tient deja ce SDL, RawInput coupe (CabinetInputReader), et
/// SDL ne lit ses reglages qu'a son demarrage. Les fonctions se prennent par leur nom dans la DLL chargee : l'assemblage
/// n'a qu'un resolveur de DllImport « SDL2 », celui du lecteur de manettes.
/// </summary>
internal static class EsManettesSdl
{
    private const uint InitJoystick = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlGuid
    {
        public ulong Bas;
        public ulong Haut;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlVersion
    {
        public byte Majeure;
        public byte Mineure;
        public byte Correctif;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnInit(uint options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FnSansArgument();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnReglage([MarshalAs(UnmanagedType.LPUTF8Str)] string nom, [MarshalAs(UnmanagedType.LPUTF8Str)] string valeur);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnNombre();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FnTexteParIndex(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate SdlGuid FnGuidParIndex(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FnOuvrir(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FnFermer(IntPtr manette);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate SdlGuid FnGuid(IntPtr manette);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FnNom(IntPtr manette);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnCompte(IntPtr manette);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnEstUneManette(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FnAjouterUnMappage([MarshalAs(UnmanagedType.LPUTF8Str)] string mappage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FnAttendre(uint millisecondes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FnVersion(out SdlVersion version);

    public static List<EsManettes.Appareil> Enumerer(string racineRetroBat, bool hidJoysticks, bool evenementsEnFond)
    {
        var (cheminSdl, _) = CabinetInputReader.ResoudreSdl(racineRetroBat);
        var dll = NativeLibrary.Load(cheminSdl);
        T Fonction<T>(string nom) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll, nom));
        T? FonctionSiPresente<T>(string nom) where T : Delegate
            => NativeLibrary.TryGetExport(dll, nom, out var adresse) ? Marshal.GetDelegateForFunctionPointer<T>(adresse) : null;

        var reglage = Fonction<FnReglage>("SDL_SetHint");
        if (!hidJoysticks)
        {
            reglage("SDL_JOYSTICK_HIDAPI", "0");
        }
        reglage("SDL_JOYSTICK_HIDAPI_WII", "0");
        reglage("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "0");
        reglage("SDL_JOYSTICK_HIDAPI_PS5_PLAYER_LED", "0");
        reglage("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", evenementsEnFond ? "1" : "0");
        if (Fonction<FnInit>("SDL_InitSubSystem")(InitJoystick) != 0)
        {
            throw new InvalidOperationException("SDL_InitSubSystem a echoue");
        }

        var quitter = Fonction<FnSansArgument>("SDL_Quit");
        try
        {
            ChargerLesMappages(racineRetroBat, Fonction<FnAjouterUnMappage>("SDL_GameControllerAddMapping"));

            // Le fil HIDAPI de SDL enumere apres le demarrage : ES attend que le compte se stabilise (500 ms au plus).
            var nombre = Fonction<FnNombre>("SDL_NumJoysticks");
            var attendre = Fonction<FnAttendre>("SDL_Delay");
            var pomper = Fonction<FnSansArgument>("SDL_PumpEvents");
            var precedent = -1;
            var stable = nombre();
            for (var essai = 0; stable != precedent && essai < 10; essai++)
            {
                precedent = stable;
                attendre(50);
                pomper();
                stable = nombre();
            }

            Fonction<FnVersion>("SDL_GetVersion")(out var version);
            var cheminParIndex = FonctionSiPresente<FnTexteParIndex>("SDL_JoystickPathForIndex");
            var avecChemins = cheminParIndex is not null && (version.Majeure > 2 || (version.Majeure == 2 && version.Mineure >= 24));
            return Lire(stable, avecChemins ? cheminParIndex : null,
                Fonction<FnGuidParIndex>("SDL_JoystickGetDeviceGUID"),
                Fonction<FnOuvrir>("SDL_JoystickOpen"),
                Fonction<FnFermer>("SDL_JoystickClose"),
                Fonction<FnGuid>("SDL_JoystickGetGUID"),
                Fonction<FnNom>("SDL_JoystickName"),
                Fonction<FnCompte>("SDL_JoystickNumButtons"),
                Fonction<FnCompte>("SDL_JoystickNumHats"),
                Fonction<FnCompte>("SDL_JoystickNumAxes"),
                Fonction<FnEstUneManette>("SDL_IsGameController"));
        }
        finally
        {
            quitter();
        }
    }

    private static List<EsManettes.Appareil> Lire(
        int nombre, FnTexteParIndex? cheminParIndex, FnGuidParIndex guidParIndex, FnOuvrir ouvrir, FnFermer fermer,
        FnGuid guidDe, FnNom nomDe, FnCompte boutons, FnCompte chapeaux, FnCompte axes, FnEstUneManette estUneManette)
    {
        // SDL peut montrer deux fois la meme manette (RAWINPUT et XInput) : la vue XInput n'a pas de chemin, la vue
        // RAWINPUT du meme constructeur et du meme produit en a un. ES ecarte la premiere.
        var chemins = new List<string>();
        var produitsAvecChemin = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < nombre; i++)
        {
            var chemin = cheminParIndex is null ? string.Empty : Marshal.PtrToStringUTF8(cheminParIndex(i)) ?? string.Empty;
            chemins.Add(chemin);
            if (chemin.Length > 0)
            {
                produitsAvecChemin.Add(Texte(guidParIndex(i)).Substring(8, 16));
            }
        }

        var appareils = new List<EsManettes.Appareil>();
        for (var index = 0; index < nombre; index++)
        {
            var manette = ouvrir(index);
            if (manette == IntPtr.Zero)
            {
                continue;
            }
            try
            {
                // SDL 2.26 et plus code un CRC du nom dans les octets 2 et 3 du GUID : ES le remet a zero.
                var guid = Texte(guidDe(manette)).ToCharArray();
                for (var i = 4; i < 8; i++)
                {
                    guid[i] = '0';
                }
                var texteGuid = new string(guid);
                if (cheminParIndex is not null && chemins[index].Length == 0 && produitsAvecChemin.Contains(texteGuid.Substring(8, 16)))
                {
                    continue;
                }

                var chemin = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "@" + texteGuid;
                if (cheminParIndex is not null && chemins[index].Length > 0)
                {
                    chemin = PeripheriquesWindows.ParentSelonEs(chemins[index]);
                }

                appareils.Add(new EsManettes.Appareil(
                    index,
                    texteGuid,
                    Marshal.PtrToStringUTF8(nomDe(manette)) ?? "Unknown joystick",
                    chemin,
                    boutons(manette),
                    chapeaux(manette),
                    axes(manette),
                    estUneManette(index) != 0));
            }
            finally
            {
                fermer(manette);
            }
        }
        return appareils;
    }

    /// <summary>
    /// Les bases de mappages qu'ES donne a SDL : celle a cote d'ES, puis celle du dossier utilisateur. Ligne a ligne,
    /// comme lui : sans plateforme, la ligne vaut pour Windows ; une autre plateforme est ignoree.
    /// </summary>
    private static void ChargerLesMappages(string racineRetroBat, FnAjouterUnMappage ajouter)
    {
        var fichiers = new[]
        {
            Path.Combine(racineRetroBat, "emulationstation", "gamecontrollerdb.txt"),
            Path.Combine(racineRetroBat, "emulationstation", ".emulationstation", "gamecontrollerdb.txt"),
        };
        foreach (var fichier in fichiers.Where(File.Exists))
        {
            foreach (var brute in File.ReadLines(fichier))
            {
                var ligne = brute.Trim();
                if (ligne.Length == 0 || ligne[0] == '#')
                {
                    continue;
                }
                if (!ligne.Contains("platform:", StringComparison.Ordinal))
                {
                    ligne = (ligne.EndsWith(',') ? ligne : ligne + ",") + "platform:Windows,";
                }
                else if (!ligne.Contains("platform:Windows,", StringComparison.Ordinal))
                {
                    continue;
                }
                ajouter(ligne);
            }
        }
    }

    /// <summary>Le GUID en hexadecimal, comme SDL_JoystickGetGUIDString : ses 16 octets dans l'ordre de la memoire.</summary>
    private static string Texte(SdlGuid guid)
        => Convert.ToHexString([.. BitConverter.GetBytes(guid.Bas), .. BitConverter.GetBytes(guid.Haut)]).ToLowerInvariant();
}
