Score verifier (bundled tool)
=============================

EN
--
APIExposeOCR.exe is the score verifier of APIExpose's silent scoring discovery:
a separate, windowless process that receives an emulator frame buffer and an
expected value from APIExpose, recognises the digits, destroys the buffer and
returns a handful of scalars (matched, confidence, region stability, candidate
count, verifier version). It has no network access, no key and writes nothing to
disk; no image ever leaves the machine.

The binary is NOT tracked by Git (only this README is). It is built from its own
repository and shipped with APIExpose:

- Build / deploy locally:  plugins/APIExposeOCR/tools/release.ps1
  Publishes a framework-dependent single-file exe (needs the .NET Runtime, already
  present on the fleet) and copies it here as APIExposeOCR.exe, with VERSION.json
  (version, SHA-256, publication date) that APIExpose logs when it starts it.
- Distribution: APIExposeOCR.exe is embedded in the APIExpose archive and installer.

FR
--
APIExposeOCR.exe est le verificateur de score de la decouverte silencieuse du
scoring d'APIExpose : un processus separe, sans fenetre, qui recoit d'APIExpose le
tampon d'image de l'emulateur et une valeur attendue, reconnait les chiffres,
detruit le tampon et renvoie quelques scalaires (trouve, confiance, stabilite de
region, nombre de candidats, version). Il n'a ni reseau, ni cle, ni ecriture
disque ; aucune image ne quitte la machine.

Le binaire n'est PAS suivi par Git (seul ce README l'est). Il est construit depuis
son propre depot et distribue avec APIExpose :

- Build / deploiement local : plugins/APIExposeOCR/tools/release.ps1
  Publie un exe single-file framework-dependent (necessite le .NET Runtime, deja
  present sur le parc) et le copie ici sous APIExposeOCR.exe, avec VERSION.json
  (version, SHA-256, date de publication) qu'APIExpose journalise au lancement.
- Distribution : APIExposeOCR.exe est embarque dans l'archive et l'installeur APIExpose.
