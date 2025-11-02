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

class PaymentIntentRequest(BaseModel):
    amount: float

@app.get("/", response_class=HTMLResponse)
async def get():
    # Load the static HTML file
    with open("index.html", "r") as f:
        html = f.read()
    
    # Replace placeholders with env var values
    import os
    replacements = {
        "{{STRIPE_PUBLISHABLE_KEY}}": os.getenv("STRIPE_PUBLISHABLE_KEY", ""),
    }
    for key, value in replacements.items():
        html = html.replace(key, value)
    
    return html

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
        
    # Check if the request was successful
    if response.status_code != 200:
        # Propagate the error response from Warp Gateway
        from fastapi import HTTPException
        raise HTTPException(status_code=response.status_code, detail=response.text)
        
    return {"status": "submitted"}

@app.post("/payment/create-intent")
async def create_payment_intent(request: PaymentIntentRequest):
    # Forward payment intent creation to Warp Gateway
    async with httpx.AsyncClient() as client:
        response = await client.post(
            "http://localhost:5000/payment/create-intent/submit",
            json=request.dict(),
            headers={"X-JWT-Email": "demo@memoryalpha.local"}
        )
    
    # Check if the request was successful
    if response.status_code != 200:
        from fastapi import HTTPException
        raise HTTPException(status_code=response.status_code, detail=response.text)
    
    return response.json()

@app.post("/webhook")
async def webhook(payload: dict):
    global connections
    try:
        print(f"Webhook received: {payload}")
        print(f"Active connections: {len(connections)}")
        
        # Check if this is a payment webhook
        if payload.get("status") == "payment_processed":
            print("Processing payment webhook")
            message = {
                "status": "payment_processed",
                "quota_added": payload.get("quota_increase", payload.get("quota_added")),
                "quota_name": payload.get("quota_name", "credits")
            }
        else:
            # Extract the result from the webhook payload (for regular chat responses)
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