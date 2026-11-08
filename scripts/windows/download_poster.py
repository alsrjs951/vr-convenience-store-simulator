"""Fetch the official Nerfstudio team's poster mirror when Drive is unavailable."""
import concurrent.futures
import json
import pathlib
import sys
import urllib.request

root = pathlib.Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
incomplete = root / ".poster-download-incomplete"
incomplete.touch()
repo = "https://huggingface.co/datasets/nerfstudioteam/datasets/resolve/main/"
api = "https://huggingface.co/api/datasets/nerfstudioteam/datasets/tree/main/poster?recursive=true&limit=1000"
with urllib.request.urlopen(api, timeout=60) as response:
    files = [item for item in json.load(response) if item["type"] == "file"]


def download(item):
    path = (root / item["path"]).resolve()
    if not path.is_relative_to(root):
        raise ValueError("Unexpected dataset path")
    if path.exists() and path.stat().st_size == item["size"]:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".part")
    with urllib.request.urlopen(repo + item["path"], timeout=120) as response:
        with temporary.open("wb") as output:
            while chunk := response.read(1024 * 1024):
                output.write(chunk)
    if temporary.stat().st_size != item["size"]:
        raise ValueError(f"Incomplete download: {path}")
    temporary.replace(path)


with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
    for number, _ in enumerate(pool.map(download, files), 1):
        if number % 25 == 0 or number == len(files):
            print(f"Poster: {number}/{len(files)} files", flush=True)

# This mirror contains 100 images but lists 226 source camera frames.
# Preserve its original metadata, then make the local smoke-test subset explicit.
dataset = root / "poster"
metadata = dataset / "transforms.json"
original = dataset / "transforms.source.json"
if not original.exists():
    original.write_bytes(metadata.read_bytes())
data = json.loads(original.read_text(encoding="utf-8"))
source_count = len(data["frames"])
data["frames"] = [
    frame for frame in data["frames"]
    if all((dataset / folder / pathlib.Path(frame["file_path"]).name).is_file()
           for folder in ("images", "images_2", "images_4", "images_8"))
]
if len(data["frames"]) < 10:
    raise ValueError("Insufficient complete frames for the smoke test")
metadata.write_text(json.dumps(data, indent=2), encoding="utf-8")
print(f"Local smoke subset: {len(data['frames'])}/{source_count} source frames", flush=True)
incomplete.unlink()
