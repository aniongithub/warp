from fastapi import FastAPI, Request

app = FastAPI()

@app.api_route("/{path:path}", methods=["GET", "POST", "PUT", "DELETE", "PATCH"])
async def echo(request: Request, path: str):
    # Print all headers to the console
    for k, v in request.headers.items():
        print(f"Header: {k} = {v}")
    body = await request.body()
    return {
        "method": request.method,
        "path": path,
        "headers": dict(request.headers),
        "query": dict(request.query_params),
        "body": body.decode("utf-8")
    }
