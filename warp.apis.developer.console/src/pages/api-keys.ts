import { createClient, Session } from '@supabase/supabase-js';

// Use the same env vars as index.ts
const SUPABASE_URL = process.env.SUPABASE_URL!;
const SUPABASE_ANON_KEY = process.env.SUPABASE_ANON_KEY!;
const WARP_DEVELOPER_URL = process.env.WARP_DEVELOPER_URL!;
const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

interface ApiKey {
  id: string;
  key: string;
  owner: string;
  isActive: boolean;
  permissions: string[];
  rateLimitHz: number;
  lastUsed: string;
  lastRate: number;
}

function maskKey(key: string) {
  if (!key) return '';
  return key.length > 8 ? key.slice(0, 4) + '****' + key.slice(-4) : '****';
}

function renderEmptyState(list: HTMLElement) {
  list.innerHTML = `<div class="empty-state">No API keys yet.</div>`;
}

function renderPermissions(key: ApiKey, onChange: (perm: string, checked: boolean) => void): HTMLElement {
  const container = document.createElement('div');
  container.className = 'api-key-permissions';
  key.permissions.forEach(perm => {
    const label = document.createElement('label');
    label.className = 'perm-label';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.value = perm;
    checkbox.checked = true;
    if (perm === 'developer') {
      checkbox.disabled = true;
    }
    checkbox.addEventListener('change', (e) => {
      onChange(perm, (e.target as HTMLInputElement).checked);
    });
    label.appendChild(checkbox);
    label.appendChild(document.createTextNode(' ' + perm));
    container.appendChild(label);
  });
  return container;
}

function renderKeys(keys: ApiKey[], list: HTMLElement) {
  list.innerHTML = '';
  if (!keys.length) {
    renderEmptyState(list);
    return;
  }
  keys.forEach(key => {
    const card = document.createElement('div');
    card.className = 'api-key-card';
    card.innerHTML = `
      <div class="api-key-label">Owner: ${key.owner ?? ''}</div>
      <div class="api-key-value">${maskKey(key.key)}</div>
      <button class="copy-btn" data-key="${key.key}">Copy</button>
      <button class="delete-btn" data-id="${key.id}">Delete</button>
    `;
    // Permissions checkboxes
    const permBox = renderPermissions(key, async (perm, checked) => {
      if (perm === 'developer') return; // can't remove developer
      const newPerms = checked
        ? [...key.permissions, perm]
        : key.permissions.filter(p => p !== perm);
      try {
        await updateApiKeyPermissions(key.id, newPerms);
        key.permissions = newPerms;
        renderKeys(keys, list); // re-render
      } catch {
        alert('Failed to update permissions.');
      }
    });
    card.appendChild(permBox);
    // Copy button
    card.querySelector('.copy-btn')?.addEventListener('click', () => {
      navigator.clipboard.writeText(key.key);
    });
    // Delete button
    card.querySelector('.delete-btn')?.addEventListener('click', async () => {
      if (confirm('Delete this API key?')) {
        await deleteApiKey(key.id);
        await refreshKeys();
      }
    });
    list.appendChild(card);
  });
}

async function getJwt(): Promise<string | null> {
  const { data } = await supabase.auth.getSession();
  return data.session?.access_token || null;
}

async function fetchApiKeys(): Promise<ApiKey[]> {
  const jwt = await getJwt();
  if (!jwt) throw new Error('No JWT available');
  const res = await fetch(`${WARP_DEVELOPER_URL}/developer/api-keys`, {
    headers: { 'Authorization': `Bearer ${jwt}` }
  });
  if (!res.ok) throw new Error('Failed to fetch API keys');
  return await res.json();
}

async function createApiKey(): Promise<ApiKey> {
  const jwt = await getJwt();
  const res = await fetch(`${WARP_DEVELOPER_URL}/developer/api-keys`, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${jwt}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({})
  });
  if (!res.ok) throw new Error('Failed to create API key');
  return await res.json();
}

async function deleteApiKey(id: string) {
  const jwt = await getJwt();
  const res = await fetch(`${WARP_DEVELOPER_URL}/developer/api-keys/${id}`, {
    method: 'DELETE',
    headers: { 'Authorization': `Bearer ${jwt}` }
  });
  if (!res.ok) throw new Error('Failed to delete API key');
}

async function updateApiKeyPermissions(id: string, permissions: string[]) {
  const jwt = await getJwt();
  const res = await fetch(`${WARP_DEVELOPER_URL}/developer/api-keys/${id}/permissions`, {
    method: 'PUT',
    headers: {
      'Authorization': `Bearer ${jwt}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({ permissions })
  });
  if (!res.ok) throw new Error('Failed to update permissions');
}

function showCreateDialog() {
  // Directly create the key, no dialog
  createApiKey()
    .then(async (key) => {
      await refreshKeys();
      // Optionally, highlight the new key or show a toast
    })
    .catch(() => {
      alert('Failed to create key.');
    });
}

async function refreshKeys() {
  const apiKeyList = document.getElementById('api-key-list');
  if (!apiKeyList) return;
  apiKeyList.innerHTML = '<div class="loading">Loading...</div>';
  try {
    const keys = await fetchApiKeys();
    renderKeys(keys, apiKeyList);
  } catch (e) {
    apiKeyList.innerHTML = '<div class="error">Failed to load API keys.</div>';
  }
}

export function init() {
  // Mount the API key management UI into the root
  const root = document.getElementById('api-keys-root');
  if (!root) return;
  root.innerHTML = `
    <div style="display: flex; justify-content: flex-end; margin-bottom: 1em;">
      <button id="create-api-key-btn" class="primary-btn">Create API Key</button>
    </div>
    <div id="api-key-list"></div>
  `;
  const apiKeyList = document.getElementById('api-key-list');
  const createBtn = document.getElementById('create-api-key-btn');
  if (createBtn) {
    createBtn.addEventListener('click', showCreateDialog);
  }
  refreshKeys();
}
