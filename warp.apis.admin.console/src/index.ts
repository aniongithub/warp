import './index.css';
import { createClient } from '@supabase/supabase-js';

const SUPABASE_URL = process.env.SUPABASE_URL!;
const SUPABASE_ANON_KEY = process.env.SUPABASE_ANON_KEY!;
const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

async function checkAuth() {
  const { data: { session } } = await supabase.auth.getSession();
  const app = document.getElementById('app');
  if (!app) return;

  function renderSignIn(show: boolean) {
    let btn = document.getElementById('app-signin-btn') as HTMLButtonElement | null;
    if (show) {
      if (!btn) {
        btn = document.createElement('button');
        btn.id = 'app-signin-btn';
        btn.className = 'auth-btn';
        btn.textContent = 'Sign In';
        btn.style.marginTop = '2em';
        btn.onclick = () => {
          let dropdown = document.getElementById('auth-dropdown');
          if (!dropdown) {
            dropdown = document.createElement('div');
            dropdown.id = 'auth-dropdown';
            dropdown.className = 'dropdown-content';
            document.body.appendChild(dropdown);
          }
          dropdown.innerHTML = `
            <form id="login-form" style="padding:0.5em 1em;">
              <input type="email" id="email" placeholder="Enter your email" required style="padding:0.5em;width:90%;margin-bottom:1em;" />
              <button type="submit" style="padding:0.5em 1em;">Send Magic Link</button>
              <div id="login-message" style="margin-top:0.5em;"></div>
            </form>
          `;
          dropdown.classList.add('show');
          const form = document.getElementById('login-form') as HTMLFormElement;
          form.onsubmit = async (e) => {
            e.preventDefault();
            const email = (document.getElementById('email') as HTMLInputElement).value;
            const { error } = await supabase.auth.signInWithOtp({ email });
            const msg = document.getElementById('login-message');
            if (error) {
              msg!.textContent = 'Error: ' + error.message;
            } else {
              msg!.textContent = 'Check your email for a magic link!';
            }
          };
        };
        (app as HTMLElement).appendChild(btn);
      } else {
        btn.style.display = '';
      }
    } else if (btn) {
      btn.style.display = 'none';
    }
  }

  if (!session) {
    app.innerHTML = '<h2>Admin Console</h2>';
    renderSignIn(true);
    return;
  }

  // Signed in: render tabs for users, etc.
  app.innerHTML = `
    <div class="navbar">
      <a href="#users" class="navbar-link">Users</a>
      <button id="auth-btn" class="auth-btn">${session.user.email || 'Account'}</button>
      <div id="dropdown-content" class="dropdown-content"></div>
    </div>
    <main id="main-content"></main>
  `;
  const authBtn = document.getElementById('auth-btn') as HTMLButtonElement;
  const dropdown = document.getElementById('dropdown-content');
  const dropdownDiv = document.getElementById('auth-btn')?.parentElement;

  authBtn.onclick = () => {
    if (dropdown) {
      dropdown.innerHTML = `
        <div style='padding:0.7em 1.2em;'>Signed in as <b>${session.user.email}</b></div>
        <button class='dropdown-item' id='signout-btn'>Sign out</button>
      `;
      dropdown.classList.toggle('show');
      const signoutBtn = document.getElementById('signout-btn');
      if (signoutBtn) {
        signoutBtn.addEventListener('click', async () => {
          await supabase.auth.signOut();
          window.location.reload();
        });
      }
    }
  };

  // Tab navigation logic
  function getPageFromHash(): 'users' {
    return 'users';
  }
  async function loadPage(page: 'users') {
    const mainContent = document.getElementById('main-content');
    if (!mainContent) return;
    if (page === 'users') {
      // Fetch users from admin API
      const resp = await fetch('/admin/users', { credentials: 'include' });
      const users = await resp.json();
      mainContent.innerHTML = `
        <h2>Users</h2>
        <table>
          <thead><tr><th>ID</th><th>Email</th><th>Permissions</th><th>Action</th></tr></thead>
          <tbody id="users-table-body"></tbody>
        </table>
      `;
      const usersTable = document.getElementById('users-table-body');
      if (usersTable) {
        usersTable.innerHTML = users.map((u: any) => `
          <tr>
            <td>${u.id}</td>
            <td>${u.email}</td>
            <td><input type="text" value="${u.permissions}" data-userid="${u.id}" class="perm-input" /></td>
            <td><button data-userid="${u.id}" class="save-btn">Save</button></td>
          </tr>
        `).join('');
        document.querySelectorAll('.save-btn').forEach(btn => {
          btn.addEventListener('click', async (e) => {
            const userId = (e.target as HTMLElement).getAttribute('data-userid');
            const input = document.querySelector(`input.perm-input[data-userid='${userId}']`) as HTMLInputElement;
            const newPerms = input.value;
            await fetch(`/admin/users/${userId}/permissions`, {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ permissions: newPerms }),
              credentials: 'include'
            });
            alert('Permissions updated');
          });
        });
      }
    }
  }
  window.addEventListener('hashchange', () => loadPage(getPageFromHash()));
  loadPage(getPageFromHash());
}

checkAuth();
