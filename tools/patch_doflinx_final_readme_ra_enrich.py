from pathlib import Path

path = Path(r"C:\Users\vince\Downloads\DOFLinx_V909\API\FINAL\README.md")
text = path.read_text(encoding="utf-8")
old = "Ensuite seulement, une passe RA complete les jeux absents, en preservant les `.MEM` deja generes par DOFLinx."
new = (
    "Ensuite seulement, une passe RA enrichit les `.MEM` deja generes par DOFLinx sans les ecraser "
    "(DOFLinx garde la priorite en cas de conflit), et complete les jeux absents."
)
if old not in text:
    raise SystemExit("README arcade DOFLinx/RA paragraph not found")
path.write_text(text.replace(old, new), encoding="utf-8")
