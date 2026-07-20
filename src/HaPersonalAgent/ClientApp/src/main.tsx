import { render } from 'preact';
import { App } from './app';
import './styles.css';

// Что: точка входа SPA.
// Зачем: смонтировать Preact-приложение в контейнер, который отдаёт index.html.
// Как: рендерит <App/> в #app.
const root = document.getElementById('app');
if (root) {
  render(<App />, root);
}
