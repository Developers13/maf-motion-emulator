from pathlib import Path

import shutil

assets_dir = Path(__file__).parent.parent.resolve() / "assets"


def configure_ocr_model():
    ocr_dir = assets_dir / "resource" / "model" / "ocr"
    if ocr_dir.exists() and any(ocr_dir.iterdir()):
        # OCR model already provided (e.g. downloaded from the ppocr_v6 zip in CI)
        print("Found existing OCR directory, skipping default OCR model import.")
        return

    common_assets_ocr = assets_dir / "MaaCommonAssets" / "OCR"
    if not common_assets_ocr.exists():
        print(f"File Not Found: {common_assets_ocr}")
        print("Provide the OCR model either via the MaaCommonAssets submodule")
        print("or by extracting ppocr_v6-small.zip into assets/resource/model/ocr.")
        exit(1)

    shutil.copytree(
        common_assets_ocr / "ppocr_v6" / "small",
        ocr_dir,
        dirs_exist_ok=True,
    )


if __name__ == "__main__":
    configure_ocr_model()

    print("OCR model configured.")
