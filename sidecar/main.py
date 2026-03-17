"""
X-Ray face recognition sidecar.

Single responsibility: receive a video frame + actor image paths,
return the names of actors whose faces appear in the frame.

The C# plugin starts this process and calls it over localhost HTTP.
"""

import argparse
import logging
import sys
from pathlib import Path

import uvicorn
from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from matcher import FaceMatcher

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    stream=sys.stdout,
)
log = logging.getLogger("xray-sidecar")

app = FastAPI(title="X-Ray Sidecar", docs_url=None, redoc_url=None)

_matcher: FaceMatcher | None = None


# ------------------------------------------------------------------
# Request / response models
# ------------------------------------------------------------------

class ActorRequest(BaseModel):
    name: str
    image_b64: str


class MatchRequest(BaseModel):
    frame_b64: str
    actors: list[ActorRequest]
    tolerance: float = 0.55
    confidence_threshold: float = 0.60


class MatchResponse(BaseModel):
    matches: list[str]


# ------------------------------------------------------------------
# Endpoints
# ------------------------------------------------------------------

@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/match", response_model=MatchResponse)
def match(req: MatchRequest):
    if _matcher is None:
        return JSONResponse(status_code=503, content={"detail": "Matcher not initialised"})

    try:
        import base64
        import numpy as np
        from PIL import Image
        import io

        frame_bytes = base64.b64decode(req.frame_b64)
        image = Image.open(io.BytesIO(frame_bytes)).convert("RGB")
        frame_array = np.array(image)
    except Exception as e:
        log.warning("Failed to decode frame: %s", e)
        return MatchResponse(matches=[])

    actors = {a.name: a.image_b64 for a in req.actors}
    matches = _matcher.match(
        frame=frame_array,
        actors=actors,
        tolerance=req.tolerance,
        confidence_threshold=req.confidence_threshold,
    )

    return MatchResponse(matches=matches)


# ------------------------------------------------------------------
# Entry point
# ------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="X-Ray face recognition sidecar")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8756)
    parser.add_argument("--cache-dir", default="./cache", dest="cache_dir",
                        help="Directory for persisted face encoding cache (.pkl files)")
    args = parser.parse_args()

    global _matcher
    cache_path = Path(args.cache_dir)
    cache_path.mkdir(parents=True, exist_ok=True)
    _matcher = FaceMatcher(cache_dir=cache_path)

    log.info("Starting X-Ray sidecar on %s:%d (cache: %s)", args.host, args.port, cache_path)
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
