import { mount } from 'svelte';
import './style.css';

const { default: Page } = await (location.pathname === '/reports' ? import('./Reports.svelte') : import('./App.svelte'));
mount(Page, { target: document.getElementById('app')! });
