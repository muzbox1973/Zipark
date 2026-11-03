"""Builds installer/Zipark.Installer/License.rtf (the MSI's license page) from the verbatim LICENSE file.

A Korean introduction comes first; the English license text that follows is the legally binding one.
Non-ASCII characters are written as RTF \\uN escapes so the file stays plain ASCII.
"""
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BS = chr(92)


def esc(text: str) -> str:
    out = []
    for ch in text:
        if ch in (BS, "{", "}"):
            out.append(BS + ch)
        elif ord(ch) < 128:
            out.append(ch)
        else:
            code = ord(ch)
            if code > 32767:
                code -= 65536  # RTF \u takes a signed 16-bit value
            out.append(f"{BS}u{code}?")
    return "".join(out)


def para(text: str, font: int, size: int, bold: bool = False) -> str:
    b = BS + "b " if bold else ""
    b_end = BS + "b0 " if bold else ""
    return f"{BS}pard{BS}sa120{BS}f{font}{BS}fs{size} {b}{esc(text)}{b_end}{BS}par\n"


intro_ko = [
    ("Zipark 라이선스 안내", True),
    ("Zipark(집파크)는 개발자 GPT PARK가 만든 무료 오픈소스 압축 프로그램이며 MIT 라이선스로 배포됩니다. "
     "개인과 기업 모두 무료로 사용할 수 있고, 광고나 구독이 없으며, 파일을 외부로 보내지 않습니다.", False),
    ("함께 설치되는 제3자 구성요소는 각자의 라이선스를 따릅니다: 7-Zip(LGPL-2.1-or-later, unRAR 제한 포함), "
     "WinFsp .NET 바인딩(GPLv3 + FLOSS 예외), Microsoft .NET(MIT). 자세한 내용은 설치 폴더의 THIRD_PARTY_NOTICES.md에 있습니다.", False),
    ("아래 영문 라이선스 원문이 법적 효력을 가집니다. 내용에 동의하면 \"동의함\"을 선택하고 다음으로 진행하세요.", False),
    ("공식 사이트: https://gptparkai.com/   유튜브: https://www.youtube.com/@AIFACT-GPTPARK", False),
]

src = open(os.path.join(ROOT, "LICENSE"), encoding="utf-8").read()
body = []
for text, bold in intro_ko:
    body.append(para(text, 0, 18, bold))
body.append(f"{BS}pard{BS}sa60{BS}par\n")
for line in src.splitlines():
    body.append(f"{BS}pard{BS}f1{BS}fs16 {esc(line)}{BS}par\n")

rtf = (
    "{" + BS + "rtf1" + BS + "ansi" + BS + "ansicpg949" + BS + "deff0" + BS + "uc1"
    + "{" + BS + "fonttbl{" + BS + "f0" + BS + "fnil" + BS + "fcharset129 Malgun Gothic;}{" + BS + "f1" + BS + "fmodern Consolas;}}\n"
    + "".join(body)
    + "}"
)
out = os.path.join(ROOT, "installer", "Zipark.Installer", "License.rtf")
with open(out, "w", encoding="ascii", newline="\r\n") as fh:
    fh.write(rtf)
print("wrote", out, len(rtf))
