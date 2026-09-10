import { mount } from 'svelte';
import 'layerchart/core.css';
import './style.css';

const { default: Page } = await (location.pathname === '/reports' ? import('./Reports.svelte') : location.pathname === '/history' ? import('./History.svelte') : import('./App.svelte'));
mount(Page, { target: document.getElementById('app')! });
