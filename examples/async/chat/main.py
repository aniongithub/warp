from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import HTMLResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
import httpx
import json
import asyncio
from typing import Set

app = FastAPI()

# Store active WebSocket connections
connections: Set[WebSocket] = set()

class QuestionRequest(BaseModel):
    question: str

@app.get("/", response_class=HTMLResponse)
async def get():
    with open("index.html", "r") as f:
        return f.read()

@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    connections.add(websocket)
    print(f"WebSocket connected. Total connections: {len(connections)}")
    try:
        while True:
            # Keep connection alive
            await websocket.receive_text()
    except WebSocketDisconnect:
        connections.remove(websocket)
        print(f"WebSocket disconnected. Total connections: {len(connections)}")

@app.post("/ask")
async def ask_question(request: QuestionRequest):
    question = request.question
    
    # Submit to Warp Gateway - using GET with query parameters as per OpenAPI spec
    async with httpx.AsyncClient() as client:
        response = await client.get(
            "http://localhost:5000/examples/async/memoryalpha/rag/ask/submit",
            params={"question": question},
            headers={"X-JWT-Email": "demo@memoryalpha.local"}
        )
        
    return {"status": "submitted"}

@app.post("/webhook")
async def webhook(payload: dict):
    global connections
    try:
        print(f"Webhook received: {payload}")
        print(f"Active connections: {len(connections)}")
        
        # Extract the result from the webhook payload
        result_json = payload.get("result", "{}")
        
        try:
            # Parse the result JSON string
            result_data = json.loads(result_json)
            answer = result_data.get("answer", "No answer received")
        except json.JSONDecodeError as e:
            print(f"JSON decode error: {e}")
            # Fallback if result is not valid JSON
            answer = result_json if result_json else "No answer received"
        
        print(f"Extracted answer: {answer}")
        
        # Send to all connected clients
        message = {"answer": answer}
        disconnected = set()
        
        for connection in connections:
            try:
                print(f"Sending message to connection: {connection}")
                await connection.send_text(json.dumps(message))
            except Exception as e:
                print(f"Failed to send to connection: {e}")
                disconnected.add(connection)
        
        # Clean up disconnected clients
        connections -= disconnected
        print(f"Connections after cleanup: {len(connections)}")
        
        return {"status": "ok"}
        
    except Exception as e:
        print(f"Webhook handler error: {e}")
        import traceback
        traceback.print_exc()
        return {"status": "error", "message": str(e)}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8888)