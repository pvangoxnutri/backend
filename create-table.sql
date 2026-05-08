-- Direct SQL for page_views table
CREATE TABLE IF NOT EXISTS public.page_views (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  country text,
  device text,
  created_at timestamp with time zone DEFAULT now()
);

ALTER TABLE public.page_views ENABLE ROW LEVEL SECURITY;

CREATE POLICY IF NOT EXISTS "page_views_insert" ON public.page_views
  FOR INSERT TO anon WITH CHECK (true);

CREATE POLICY IF NOT EXISTS "page_views_select" ON public.page_views
  FOR SELECT TO authenticated WITH CHECK (true);

CREATE INDEX IF NOT EXISTS page_views_created_at ON public.page_views(created_at);
CREATE INDEX IF NOT EXISTS page_views_country ON public.page_views(country);
CREATE INDEX IF NOT EXISTS page_views_device ON public.page_views(device);
