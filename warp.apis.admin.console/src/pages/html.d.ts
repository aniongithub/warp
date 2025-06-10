// Allow importing HTML files as modules for TypeScript
// This matches the Developer Console's approach

declare module '*.html' {
  const value: string;
  export default value;
}
