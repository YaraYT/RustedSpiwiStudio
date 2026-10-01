from pathlib import Path
import zipfile
import hashlib
import sys


ROOT = Path(__file__).resolve().parent
OUTPUT = ROOT / "RustedSpiwiStudio-0.1.5-win-x64.zip"

REQUIRED = [
    Path("RustedShpizhionStudio.exe"),
    Path("libSkiaSharp.dll"),
    Path("libHarfBuzzSharp.dll"),
    Path("av_libglesv2.dll"),
    Path("resources/rusted_warfare_docs.db"),
]


def find_publish_dir():
    candidates = []

    for exe in ROOT.rglob("RustedShpizhionStudio.exe"):
        if exe.parent.name.lower() == "publish":
            candidates.append(exe.parent)

    if not candidates:
        return None

    candidates.sort(
        key=lambda path: (path / "RustedShpizhionStudio.exe").stat().st_mtime,
        reverse=True,
    )

    return candidates[0]


def main():
    print("=== RustedSpiwiStudio Packager ===")
    print(f"Project: {ROOT}")
    print()

    publish_dir = find_publish_dir()

    if publish_dir is None:
        print("ERROR: RustedShpizhionStudio.exe not found.")
        print(f"Searched: {ROOT}")
        return 1

    print("Publish directory:")
    print(f"  {publish_dir}")
    print()

    missing = []

    for relative_path in REQUIRED:
        file_path = publish_dir / relative_path

        if file_path.exists():
            print(f"[OK]   {relative_path}")
        else:
            print(f"[MISS] {relative_path}")
            missing.append(relative_path)

    if missing:
        print()
        print("ERROR: required files are missing.")
        return 1

    print()

    if OUTPUT.exists():
        print(f"Removing old archive: {OUTPUT.name}")
        OUTPUT.unlink()

    print("Creating ZIP...")

    with zipfile.ZipFile(
        OUTPUT,
        "w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
    ) as archive:

        for relative_path in REQUIRED:
            source = publish_dir / relative_path

            archive.write(
                source,
                arcname=relative_path.as_posix(),
            )

            print(f"  + {relative_path}")

    sha256 = hashlib.sha256()

    with OUTPUT.open("rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            sha256.update(chunk)

    print()
    print("=== DONE ===")
    print()
    print(f"ZIP:")
    print(f"  {OUTPUT}")
    print()
    print(f"Size:")
    print(f"  {OUTPUT.stat().st_size / 1024 / 1024:.2f} MB")
    print()
    print("SHA-256:")
    print(f"  {sha256.hexdigest()}")

    return 0


if __name__ == "__main__":
    sys.exit(main())