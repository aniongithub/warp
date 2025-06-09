// Entry point for Warp Developer Console
import { createClient } from '@supabase/supabase-js';

const SUPABASE_URL = process.env.SUPABASE_URL!;
const SUPABASE_ANON_KEY = process.env.SUPABASE_ANON_KEY!;
const WARP_DEVELOPER_URL = process.env.WARP_DEVELOPER_URL!;
const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

async function checkAuth() {
  const { data: { session } } = await supabase.auth.getSession();
  const authBtn = document.getElementById('auth-btn') as HTMLButtonElement;
  const dropdown = document.getElementById('dropdown-content');
  const dropdownDiv = document.getElementById('auth-dropdown');
  const app = document.getElementById('app');

  function closeDropdown(e?: MouseEvent) {
    if (dropdown && dropdown.classList.contains('show')) {
      dropdown.classList.remove('show');
    }
  }

  // Helper to render the sign-in button in the app area
  function renderAppSignInButton(show: boolean) {
    let btn = document.getElementById('app-signin-btn') as HTMLButtonElement | null;
    if (show) {
      if (!btn) {
        btn = document.createElement('button');
        btn.id = 'app-signin-btn';
        btn.className = 'auth-btn';
        btn.textContent = 'Sign In';
        btn.style.marginTop = '2em';
        btn.onclick = () => {
          if (dropdown) {
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
          }
        };
        app?.appendChild(btn);
      } else {
        btn.style.display = '';
      }
    } else if (btn) {
      btn.style.display = 'none';
    }
  }

  if (!session) {
    // Not signed in
    if (authBtn) {
      authBtn.textContent = 'Sign In';
      authBtn.onclick = () => {
        if (dropdown) {
          dropdown.innerHTML = `
            <form id="login-form" style="padding:0.5em 1em;">
              <input type="email" id="email" placeholder="Enter your email" required style="padding:0.5em;width:90%;margin-bottom:1em;" />
              <button type="submit" style="padding:0.5em 1em;">Send Magic Link</button>
              <div id="login-message" style="margin-top:0.5em;"></div>
            </form>
          `;
          dropdown.classList.toggle('show');
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
        }
      };
    }
    renderAppSignInButton(true);
    // Hide dropdown on click outside
    document.addEventListener('click', function handler(e) {
      if (dropdownDiv && !dropdownDiv.contains(e.target as Node)) {
        closeDropdown();
        document.removeEventListener('click', handler);
      }
    });
    return;
  }

  // Signed in
  renderAppSignInButton(false);
  const user = session.user;
  if (authBtn && dropdown) {
    authBtn.textContent = user.email || 'Account';
    authBtn.onclick = () => {
      dropdown.innerHTML = `
        <div style='padding:0.7em 1.2em;'>Signed in as <b>${user.email}</b></div>
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
    };
    // Hide dropdown on click outside
    document.addEventListener('click', function handler(e) {
      if (dropdownDiv && !dropdownDiv.contains(e.target as Node)) {
        closeDropdown();
        document.removeEventListener('click', handler);
      }
    });
  }
}

// Entry point for Warp Developer Console
console.log('Warp Developer Console loaded.');

document.addEventListener('DOMContentLoaded', () => {
  checkAuth();

  // SPA router logic: load HTML fragment and JS module for each page
  const mainContent = document.getElementById('main-content');
  const pages = {
    'home' : { html: './pages/home.html' },
    'api-keys': { html: './pages/api-keys.html' },
  } as const;
  type PageKey = keyof typeof pages;

  function getPageFromHash(): PageKey {
    const hash = window.location.hash.replace(/^#/, '');
    return (hash && hash in pages ? hash : 'home') as PageKey;
  }

  async function loadPage(page: PageKey) {
    if (!mainContent || !(page in pages)) {
      mainContent && (mainContent.textContent = 'Page not found');
      return;
    }
    // Fetch HTML fragment at runtime
    const res = await fetch(`./pages/${page}.html`);
    if (!res.ok) {
      mainContent.textContent = 'Failed to load page.';
      return;
    }
    const html = await res.text();
    // Parse HTML and inject nodes (no innerHTML)
    const temp = document.createElement('template');
    temp.innerHTML = html.trim();
    while (mainContent.firstChild) mainContent.removeChild(mainContent.firstChild);
    Array.from(temp.content.childNodes).forEach(node => mainContent.appendChild(node));
    // Use static import mapping for page modules
    const pageModules = {
      home: () => import('./pages/home'),
      'api-keys': () => import('./pages/api-keys'),
    };
    try {
      const mod = await pageModules[page]();
      if (mod && typeof mod.init === 'function') {
        mod.init();
      }
    } catch (e) {
      console.error(`Failed to load module for page "${page}":`, e);
    }
  }

  // Listen for hash changes for navigation
  window.addEventListener('hashchange', () => {
    const page = getPageFromHash();
    loadPage(page);
  });

  // Set up nav link handlers (optional, for non-hash links)
  const navLinks = document.querySelectorAll('.navbar-link');
  navLinks.forEach(link => {
    link.addEventListener('click', (e) => {
      const href = (link as HTMLAnchorElement).getAttribute('href');
      if (href && href.startsWith('#')) {
        e.preventDefault();
        window.location.hash = href;
      }
    });
  });

  // Show initial page based on hash or default to home
  loadPage(getPageFromHash());
});
