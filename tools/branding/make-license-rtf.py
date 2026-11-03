"""Builds installer/Zipark.Installer/License.rtf (shown by the MSI) from the verbatim LICENSE file."""
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BS = chr(92)


def esc(text: str) -> str:
    return text.replace(BS, BS * 2).replace("{", BS + "{").replace("}", BS + "}")


def par() -> str:
    return BS + "par "


src = open(os.path.join(ROOT, "LICENSE"), encoding="utf-8").read()
intro = [
    "Zipark - developed by GPT PARK (https://gptparkai.com/)",
    "",
    "Third-party components (7-Zip: LGPL-2.1-or-later with the unRAR restriction; WinFsp .NET binding: GPLv3 with FLOSS "
    "exception; Microsoft .NET: MIT) are listed in THIRD_PARTY_NOTICES.md in the installation folder.",
    "",
    "",
]
lines = intro + src.splitlines()
body = par().join(esc(l) for l in lines)
rtf = "{" + BS + "rtf1" + BS + "ansi" + BS + "deff0{" + BS + "fonttbl{" + BS + "f0 Consolas;}}" + BS + "fs16 " + body + "}"
out = os.path.join(ROOT, "installer", "Zipark.Installer", "License.rtf")
with open(out, "w", encoding="ascii", errors="replace") as fh:
    fh.write(rtf)
print("wrote", out, len(rtf))
