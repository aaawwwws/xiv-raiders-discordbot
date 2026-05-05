namespace RaidDcBot.Data;

public static class DbSchema
{
    public const string Sql = @"
--
-- PostgreSQL database dump
--

-- Dumped from database version 16.8
-- Dumped by pg_dump version 17.4

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: SCHEMA ""public""; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON SCHEMA ""public"" IS 'standard public schema';


SET default_tablespace = '';

SET default_table_access_method = ""heap"";

--
-- Name: discord_user_characters; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""discord_user_characters"" (
    ""id"" integer NOT NULL,
    ""discord_user_id"" bigint NOT NULL,
    ""lodestone_id"" bigint NOT NULL,
    ""label"" ""text"" DEFAULT 'main'::""text"" NOT NULL,
    ""is_main"" boolean DEFAULT true NOT NULL,
    ""linked_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""discord_user_characters_label_check"" CHECK ((""label"" = ANY (ARRAY['main'::""text"", 'sub'::""text""])))
);


--
-- Name: discord_user_characters_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""discord_user_characters_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: discord_user_characters_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""discord_user_characters_id_seq"" OWNED BY ""public"".""discord_user_characters"".""id"";


--
-- Name: discord_users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""discord_users"" (
    ""id"" integer NOT NULL,
    ""discord_user_id"" bigint NOT NULL,
    ""display_name"" ""text"",
    ""discord_name"" ""text"",
    ""ffxiv_name"" ""text"",
    ""ffxiv_world"" ""text"",
    ""ffxiv_data_center"" ""text"",
    ""x_handle"" ""text"",
    ""bluesky_handle"" ""text"",
    ""bio"" ""text"",
    ""experience_points"" integer DEFAULT 0 NOT NULL,
    ""status"" ""text"" DEFAULT 'active'::""text"" NOT NULL,
    ""voice_notification_enabled"" boolean DEFAULT true NOT NULL,
    ""story_progression"" ""text"",
    ""registration_review_message_id"" bigint,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);

-- Name: albums; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""albums"" (
    ""id"" serial PRIMARY KEY,
    ""owner_discord_user_id"" bigint NOT NULL,
    ""title"" text NOT NULL,
    ""description"" text,
    ""cover_image_url"" text,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""is_auto_archive"" boolean DEFAULT false NOT NULL
);

CREATE INDEX IF NOT EXISTS ""idx_albums_owner""
    ON ""public"".""albums"" (""owner_discord_user_id"", ""updated_at"" DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ""ux_albums_owner_auto_archive""
    ON ""public"".""albums"" (""owner_discord_user_id"")
    WHERE ""is_auto_archive"";

-- Name: album_photos; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""album_photos"" (
    ""id"" serial PRIMARY KEY,
    ""album_id"" integer NOT NULL REFERENCES ""public"".""albums""(""id"") ON DELETE CASCADE,
    ""image_url"" text NOT NULL,
    ""caption"" text,
    ""sort_order"" integer DEFAULT 0 NOT NULL,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""location"" text
);

CREATE INDEX IF NOT EXISTS ""idx_album_photos_album""
    ON ""public"".""album_photos"" (""album_id"", ""sort_order"" ASC);

--
-- Name: inactive_kick_user_data_retentions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""inactive_kick_user_data_retentions"" (
    ""id"" SERIAL PRIMARY KEY,
    ""guild_id"" bigint NOT NULL,
    ""user_id"" bigint NOT NULL,
    ""delete_after"" timestamp with time zone NOT NULL,
    ""warning_notified_at"" timestamp with time zone,
    ""final_warning_notified_at"" timestamp with time zone,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ""ux_inactive_kick_user_data_retentions""
ON ""public"".""inactive_kick_user_data_retentions"" (""guild_id"", ""user_id"");

CREATE INDEX IF NOT EXISTS ""idx_inactive_kick_user_data_retentions_delete_after""
ON ""public"".""inactive_kick_user_data_retentions"" (""delete_after"");


--
-- Name: discord_users_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""discord_users_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: discord_users_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""discord_users_id_seq"" OWNED BY ""public"".""discord_users"".""id"";


--
-- Name: guild_control_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_control_panels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_control_panels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_control_panels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_control_panels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_control_panels_id_seq"" OWNED BY ""public"".""guild_control_panels"".""id"";


--
-- Name: guild_lodestone_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_lodestone_panels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_lodestone_panels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_lodestone_panels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_lodestone_panels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_lodestone_panels_id_seq"" OWNED BY ""public"".""guild_lodestone_panels"".""id"";


--
-- Name: guild_member_content_roles; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_member_content_roles"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""role_key"" ""text"" NOT NULL,
    ""label"" ""text"" NOT NULL,
    ""role_name"" ""text"" NOT NULL,
    ""button_style"" smallint DEFAULT 2 NOT NULL,
    ""button_row"" smallint DEFAULT 0 NOT NULL,
    ""sort_order"" integer DEFAULT 0 NOT NULL,
    ""is_active"" boolean DEFAULT true NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_member_content_roles_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_member_content_roles_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_member_content_roles_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_member_content_roles_id_seq"" OWNED BY ""public"".""guild_member_content_roles"".""id"";


--
-- Name: guild_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_panels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_panels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_panels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_panels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_panels_id_seq"" OWNED BY ""public"".""guild_panels"".""id"";


--
-- Name: guild_recruit_list_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_recruit_list_panels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_recruit_list_panels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_recruit_list_panels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_recruit_list_panels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_recruit_list_panels_id_seq"" OWNED BY ""public"".""guild_recruit_list_panels"".""id"";


--
-- Name: guild_recruit_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_recruit_panels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_recruit_panels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_recruit_panels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_recruit_panels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_recruit_panels_id_seq"" OWNED BY ""public"".""guild_recruit_panels"".""id"";


--
-- Name: guild_settings; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_settings"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""admin_role_id"" bigint NOT NULL,
    ""member_role_id"" bigint NOT NULL,
    ""provisional_role_id"" bigint NOT NULL,
    ""registration_channel_id"" bigint NOT NULL,
    ""registration_review_channel_id"" bigint NOT NULL,
    ""registration_approval_required"" boolean DEFAULT true NOT NULL,
    ""inactive_auto_kick_enabled"" boolean DEFAULT true NOT NULL,
    ""inactive_auto_kick_user_data_retention_days"" integer DEFAULT 7 NOT NULL,
    ""sleep_kick_enabled"" boolean DEFAULT true NOT NULL,
    ""sleep_kick_auto_detect_enabled"" boolean DEFAULT true NOT NULL,
    ""sleep_kick_admin_confirmation_required"" boolean DEFAULT false NOT NULL,
    ""lodestone_user_agent"" text,
    ""log_channel_id"" bigint NOT NULL,
    ""control_channel_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_settings_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_settings_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_settings_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_settings_id_seq"" OWNED BY ""public"".""guild_settings"".""id"";


--
-- Name: guild_stats_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""guild_stats_panels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""data_center_message_id"" bigint NOT NULL,
    ""world_message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: guild_stats_panels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""guild_stats_panels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: guild_stats_panels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""guild_stats_panels_id_seq"" OWNED BY ""public"".""guild_stats_panels"".""id"";


--
-- Name: link_tokens; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""link_tokens"" (
    ""id"" integer NOT NULL,
    ""token_id"" ""uuid"" NOT NULL,
    ""discord_user_id"" bigint NOT NULL,
    ""lodestone_id"" bigint,
    ""lodestone_url"" ""text"",
    ""label"" ""text"" DEFAULT 'main'::""text"" NOT NULL,
    ""token"" ""text"" NOT NULL,
    ""expires_at"" timestamp with time zone NOT NULL,
    ""used_at"" timestamp with time zone,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""link_tokens_label_check"" CHECK ((""label"" = ANY (ARRAY['main'::""text"", 'sub'::""text""])))
);


--
-- Name: link_tokens_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""link_tokens_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: link_tokens_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""link_tokens_id_seq"" OWNED BY ""public"".""link_tokens"".""id"";


--
-- Name: lodestone_characters; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""lodestone_characters"" (
    ""id"" integer NOT NULL,
    ""lodestone_id"" bigint NOT NULL,
    ""name"" ""text"" NOT NULL,
    ""world"" ""text"" NOT NULL,
    ""data_center"" ""text"",
    ""portrait_url"" ""text"",
    ""self_introduction"" ""text"",
    ""lodestone_url"" ""text"" NOT NULL,
    ""extras_json"" jsonb,
    ""extras_synced_at"" timestamp with time zone,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: lodestone_characters_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""lodestone_characters_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: lodestone_characters_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""lodestone_characters_id_seq"" OWNED BY ""public"".""lodestone_characters"".""id"";


--
-- Name: message_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""message_logs"" (
    ""id"" integer NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""channel_name"" ""text"",
    ""author_id"" bigint NOT NULL,
    ""author_name"" ""text"" NOT NULL,
    ""author_display_name"" ""text"",
    ""content"" ""text"" NOT NULL,
    ""attachment_count"" integer DEFAULT 0 NOT NULL,
    ""embed_count"" integer DEFAULT 0 NOT NULL,
    ""attachments_json"" jsonb,
    ""embeds_json"" jsonb,
    ""created_at"" timestamp with time zone NOT NULL,
    ""edited_at"" timestamp with time zone,
    ""deleted_at"" timestamp with time zone
);


--
-- Name: message_logs_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""message_logs_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: message_logs_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""message_logs_id_seq"" OWNED BY ""public"".""message_logs"".""id"";


--
-- Name: recruit_posts; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""recruit_posts"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""channel_name"" ""text"",
    ""message_id"" bigint NOT NULL,
    ""thread_channel_id"" bigint,
    ""thread_message_id"" bigint,
    ""status"" smallint DEFAULT 0 NOT NULL,
    ""closed_at"" timestamp with time zone,
    ""creator_user_id"" bigint NOT NULL,
    ""title"" ""text"" NOT NULL,
    ""data_center"" ""text"",
    ""schedule_text"" ""text"",
    ""schedule_at"" timestamp with time zone,
    ""reminder_sent_at"" timestamp with time zone,
    ""underfilled_decision_deadline_at"" timestamp with time zone,
    ""note"" ""text"",
    ""disallow_duplicate_jobs"" boolean DEFAULT true NOT NULL,
    ""host_flex_spec"" ""text"",
    ""host_flex_jobs_spec"" ""text"",
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: recruit_posts_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""recruit_posts_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: recruit_posts_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""recruit_posts_id_seq"" OWNED BY ""public"".""recruit_posts"".""id"";


--
-- Name: recruit_slots; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""recruit_slots"" (
    ""id"" integer NOT NULL,
    ""post_id"" integer NOT NULL,
    ""position"" ""text"" NOT NULL,
    ""required_job"" ""text"",
    ""flex_spec"" ""text"",
    ""flex_jobs_spec"" ""text"",
    ""job"" ""text"",
    ""user_id"" bigint,
    ""user_name"" ""text"",
    ""comment"" ""text"",
    ""is_closed"" boolean DEFAULT false NOT NULL,
    ""thread_message_id"" bigint,
    ""joined_at"" timestamp with time zone,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: recruit_slots_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""recruit_slots_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: recruit_slots_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""recruit_slots_id_seq"" OWNED BY ""public"".""recruit_slots"".""id"";


--
-- Name: temp_voice_channels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""temp_voice_channels"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""owner_user_id"" bigint NOT NULL,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);


--
-- Name: temp_voice_channels_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""temp_voice_channels_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: temp_voice_channels_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""temp_voice_channels_id_seq"" OWNED BY ""public"".""temp_voice_channels"".""id"";


--
-- Name: voice_sessions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE ""public"".""voice_sessions"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""user_id"" bigint NOT NULL,
    ""user_name"" ""text"" NOT NULL,
    ""joined_at"" timestamp with time zone NOT NULL,
    ""left_at"" timestamp with time zone,
    ""duration_seconds"" integer
);


--
-- Name: voice_sessions_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE ""public"".""voice_sessions_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: voice_sessions_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""voice_sessions_id_seq"" OWNED BY ""public"".""voice_sessions"".""id"";


--
-- Name: discord_user_characters id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""discord_user_characters_id_seq""'::""regclass"");


--
-- Name: discord_users id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_users"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""discord_users_id_seq""'::""regclass"");


--
-- Name: guild_control_panels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_control_panels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_control_panels_id_seq""'::""regclass"");


--
-- Name: guild_lodestone_panels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_lodestone_panels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_lodestone_panels_id_seq""'::""regclass"");


--
-- Name: guild_member_content_roles id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_member_content_roles"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_member_content_roles_id_seq""'::""regclass"");


--
-- Name: guild_panels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_panels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_panels_id_seq""'::""regclass"");


--
-- Name: guild_recruit_list_panels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_recruit_list_panels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_recruit_list_panels_id_seq""'::""regclass"");


--
-- Name: guild_recruit_panels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_recruit_panels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_recruit_panels_id_seq""'::""regclass"");


--
-- Name: guild_settings id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_settings"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_settings_id_seq""'::""regclass"");


--
-- Name: guild_stats_panels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_stats_panels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""guild_stats_panels_id_seq""'::""regclass"");


--
-- Name: link_tokens id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""link_tokens"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""link_tokens_id_seq""'::""regclass"");


--
-- Name: lodestone_characters id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""lodestone_characters"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""lodestone_characters_id_seq""'::""regclass"");


--
-- Name: message_logs id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""message_logs"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""message_logs_id_seq""'::""regclass"");


--
-- Name: recruit_posts id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_posts"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""recruit_posts_id_seq""'::""regclass"");


--
-- Name: recruit_slots id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_slots"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""recruit_slots_id_seq""'::""regclass"");


--
-- Name: temp_voice_channels id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""temp_voice_channels"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""temp_voice_channels_id_seq""'::""regclass"");


--
-- Name: voice_sessions id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""voice_sessions"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""voice_sessions_id_seq""'::""regclass"");


--
-- Name: discord_user_characters discord_user_characters_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters""
    ADD CONSTRAINT ""discord_user_characters_pkey"" PRIMARY KEY (""id"");


--
-- Name: discord_users discord_users_discord_user_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_users""
    ADD CONSTRAINT ""discord_users_discord_user_id_key"" UNIQUE (""discord_user_id"");


--
-- Name: discord_users discord_users_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_users""
    ADD CONSTRAINT ""discord_users_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_control_panels guild_control_panels_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_control_panels""
    ADD CONSTRAINT ""guild_control_panels_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_control_panels guild_control_panels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_control_panels""
    ADD CONSTRAINT ""guild_control_panels_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_lodestone_panels guild_lodestone_panels_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_lodestone_panels""
    ADD CONSTRAINT ""guild_lodestone_panels_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_lodestone_panels guild_lodestone_panels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_lodestone_panels""
    ADD CONSTRAINT ""guild_lodestone_panels_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_member_content_roles guild_member_content_roles_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_member_content_roles""
    ADD CONSTRAINT ""guild_member_content_roles_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_panels guild_panels_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_panels""
    ADD CONSTRAINT ""guild_panels_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_panels guild_panels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_panels""
    ADD CONSTRAINT ""guild_panels_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_recruit_list_panels guild_recruit_list_panels_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_recruit_list_panels""
    ADD CONSTRAINT ""guild_recruit_list_panels_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_recruit_list_panels guild_recruit_list_panels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_recruit_list_panels""
    ADD CONSTRAINT ""guild_recruit_list_panels_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_recruit_panels guild_recruit_panels_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_recruit_panels""
    ADD CONSTRAINT ""guild_recruit_panels_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_recruit_panels guild_recruit_panels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_recruit_panels""
    ADD CONSTRAINT ""guild_recruit_panels_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_settings guild_settings_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_settings""
    ADD CONSTRAINT ""guild_settings_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_settings guild_settings_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_settings""
    ADD CONSTRAINT ""guild_settings_pkey"" PRIMARY KEY (""id"");


--
-- Name: guild_stats_panels guild_stats_panels_guild_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_stats_panels""
    ADD CONSTRAINT ""guild_stats_panels_guild_id_key"" UNIQUE (""guild_id"");


--
-- Name: guild_stats_panels guild_stats_panels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_stats_panels""
    ADD CONSTRAINT ""guild_stats_panels_pkey"" PRIMARY KEY (""id"");


--
-- Name: link_tokens link_tokens_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""link_tokens""
    ADD CONSTRAINT ""link_tokens_pkey"" PRIMARY KEY (""id"");


--
-- Name: link_tokens link_tokens_token_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""link_tokens""
    ADD CONSTRAINT ""link_tokens_token_id_key"" UNIQUE (""token_id"");


--
-- Name: lodestone_characters lodestone_characters_lodestone_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""lodestone_characters""
    ADD CONSTRAINT ""lodestone_characters_lodestone_id_key"" UNIQUE (""lodestone_id"");


--
-- Name: lodestone_characters lodestone_characters_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""lodestone_characters""
    ADD CONSTRAINT ""lodestone_characters_pkey"" PRIMARY KEY (""id"");


--
-- Name: message_logs message_logs_message_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""message_logs""
    ADD CONSTRAINT ""message_logs_message_id_key"" UNIQUE (""message_id"");


--
-- Name: message_logs message_logs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""message_logs""
    ADD CONSTRAINT ""message_logs_pkey"" PRIMARY KEY (""id"");


--
-- Name: recruit_posts recruit_posts_message_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_posts""
    ADD CONSTRAINT ""recruit_posts_message_id_key"" UNIQUE (""message_id"");


--
-- Name: recruit_posts recruit_posts_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_posts""
    ADD CONSTRAINT ""recruit_posts_pkey"" PRIMARY KEY (""id"");


--
-- Name: recruit_slots recruit_slots_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_slots""
    ADD CONSTRAINT ""recruit_slots_pkey"" PRIMARY KEY (""id"");


--
-- Name: temp_voice_channels temp_voice_channels_channel_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""temp_voice_channels""
    ADD CONSTRAINT ""temp_voice_channels_channel_id_key"" UNIQUE (""channel_id"");


--
-- Name: temp_voice_channels temp_voice_channels_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""temp_voice_channels""
    ADD CONSTRAINT ""temp_voice_channels_pkey"" PRIMARY KEY (""id"");


--
-- Name: discord_user_characters ux_discord_user_characters_lodestone; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters""
    ADD CONSTRAINT ""ux_discord_user_characters_lodestone"" UNIQUE (""lodestone_id"");


--
-- Name: discord_user_characters ux_discord_user_characters_pair; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters""
    ADD CONSTRAINT ""ux_discord_user_characters_pair"" UNIQUE (""discord_user_id"", ""lodestone_id"");


--
-- Name: discord_user_characters ux_discord_user_characters_user; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters""
    ADD CONSTRAINT ""ux_discord_user_characters_user"" UNIQUE (""discord_user_id"");


--
-- Name: guild_member_content_roles ux_guild_member_content_roles; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""guild_member_content_roles""
    ADD CONSTRAINT ""ux_guild_member_content_roles"" UNIQUE (""guild_id"", ""role_key"");


--
-- Name: recruit_slots ux_recruit_slots_position; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_slots""
    ADD CONSTRAINT ""ux_recruit_slots_position"" UNIQUE (""post_id"", ""position"");


--
-- Name: recruit_slots ux_recruit_slots_user; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_slots""
    ADD CONSTRAINT ""ux_recruit_slots_user"" UNIQUE (""post_id"", ""user_id"");


--
-- Name: voice_sessions voice_sessions_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""voice_sessions""
    ADD CONSTRAINT ""voice_sessions_pkey"" PRIMARY KEY (""id"");


--
-- Name: idx_link_tokens_lodestone; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_link_tokens_lodestone"" ON ""public"".""link_tokens"" USING ""btree"" (""lodestone_id"");


--
-- Name: idx_link_tokens_user_expires; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_link_tokens_user_expires"" ON ""public"".""link_tokens"" USING ""btree"" (""discord_user_id"", ""expires_at"" DESC);


--
-- Name: idx_message_logs_author; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_message_logs_author"" ON ""public"".""message_logs"" USING ""btree"" (""author_id"");


--
-- Name: idx_message_logs_channel; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_message_logs_channel"" ON ""public"".""message_logs"" USING ""btree"" (""channel_id"");


--
-- Name: idx_message_logs_guild; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_message_logs_guild"" ON ""public"".""message_logs"" USING ""btree"" (""guild_id"");


--
-- Name: idx_temp_voice_channels_guild; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_temp_voice_channels_guild"" ON ""public"".""temp_voice_channels"" USING ""btree"" (""guild_id"");


--
-- Name: idx_temp_voice_channels_owner; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_temp_voice_channels_owner"" ON ""public"".""temp_voice_channels"" USING ""btree"" (""owner_user_id"");


--
-- Name: idx_voice_sessions_channel; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_voice_sessions_channel"" ON ""public"".""voice_sessions"" USING ""btree"" (""channel_id"");


--
-- Name: idx_voice_sessions_guild; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_voice_sessions_guild"" ON ""public"".""voice_sessions"" USING ""btree"" (""guild_id"");


--
-- Name: idx_voice_sessions_user; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_voice_sessions_user"" ON ""public"".""voice_sessions"" USING ""btree"" (""user_id"");


--
-- Name: ux_voice_sessions_open; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ""ux_voice_sessions_open"" ON ""public"".""voice_sessions"" USING ""btree"" (""guild_id"", ""user_id"") WHERE (""left_at"" IS NULL);


--
-- Name: discord_user_characters discord_user_characters_discord_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters""
    ADD CONSTRAINT ""discord_user_characters_discord_user_id_fkey"" FOREIGN KEY (""discord_user_id"") REFERENCES ""public"".""discord_users""(""discord_user_id"") ON DELETE CASCADE;


--
-- Name: discord_user_characters discord_user_characters_lodestone_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""discord_user_characters""
    ADD CONSTRAINT ""discord_user_characters_lodestone_id_fkey"" FOREIGN KEY (""lodestone_id"") REFERENCES ""public"".""lodestone_characters""(""lodestone_id"") ON DELETE CASCADE;


--
-- Name: link_tokens link_tokens_discord_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""link_tokens""
    ADD CONSTRAINT ""link_tokens_discord_user_id_fkey"" FOREIGN KEY (""discord_user_id"") REFERENCES ""public"".""discord_users""(""discord_user_id"") ON DELETE CASCADE;


--
-- Name: recruit_slots recruit_slots_post_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""recruit_slots""
    ADD CONSTRAINT ""recruit_slots_post_id_fkey"" FOREIGN KEY (""post_id"") REFERENCES ""public"".""recruit_posts""(""id"") ON DELETE CASCADE;

--
-- Name: user_reaction_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""user_reaction_logs"" (
    ""id"" integer NOT NULL,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""channel_name"" ""text"",
    ""message_id"" bigint NOT NULL,
    ""user_id"" bigint NOT NULL,
    ""emoji_key"" ""text"" NOT NULL,
    ""reacted_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL
);

--
-- Name: user_reaction_logs_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE IF NOT EXISTS ""public"".""user_reaction_logs_id_seq""
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

--
-- Name: user_reaction_logs_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE ""public"".""user_reaction_logs_id_seq"" OWNED BY ""public"".""user_reaction_logs"".""id"";

--
-- Name: user_reaction_logs id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""user_reaction_logs"" ALTER COLUMN ""id"" SET DEFAULT ""nextval""('""public"".""user_reaction_logs_id_seq""'::""regclass"");

--
-- Name: user_reaction_logs user_reaction_logs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""user_reaction_logs""
    ADD CONSTRAINT ""user_reaction_logs_pkey"" PRIMARY KEY (""id"");

--
-- Name: user_reaction_logs ux_user_reaction_logs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY ""public"".""user_reaction_logs""
    ADD CONSTRAINT ""ux_user_reaction_logs"" UNIQUE (""guild_id"", ""user_id"", ""message_id"", ""emoji_key"");

--
-- Name: idx_user_reaction_logs_guild_reacted_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_user_reaction_logs_guild_reacted_at"" ON ""public"".""user_reaction_logs"" USING ""btree"" (""guild_id"", ""reacted_at"" DESC);

--
-- Name: idx_user_reaction_logs_user; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ""idx_user_reaction_logs_user"" ON ""public"".""user_reaction_logs"" USING ""btree"" (""user_id"");

--
-- Name: guild_admin_control_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""guild_admin_control_panels"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""guild_admin_control_panels_guild_id_key"" UNIQUE (""guild_id"")
);

--
-- Name: guild_daily_activity_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""guild_daily_activity_panels"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""guild_daily_activity_panels_guild_id_key"" UNIQUE (""guild_id"")
);

--
-- Name: guild_weekly_activity_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""guild_weekly_activity_panels"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""guild_weekly_activity_panels_guild_id_key"" UNIQUE (""guild_id"")
);

--
-- Name: guild_daily_reaction_post_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""guild_daily_reaction_post_panels"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""guild_daily_reaction_post_panels_guild_id_key"" UNIQUE (""guild_id"")
);

--
-- Name: guild_weekly_reaction_post_panels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""guild_weekly_reaction_post_panels"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""guild_weekly_reaction_post_panels_pkey"" PRIMARY KEY (""id""),
    CONSTRAINT ""guild_weekly_reaction_post_panels_guild_id_key"" UNIQUE (""guild_id"")
);

--
-- Name: lotteries; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""lotteries"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""channel_id"" bigint NOT NULL,
    ""message_id"" bigint,
    ""title"" ""text"" NOT NULL,
    ""description"" ""text"",
    ""winner_count"" integer NOT NULL,
    ""status"" ""text"" NOT NULL,
    ""close_at"" timestamp with time zone NOT NULL,
    ""created_by_user_id"" bigint NOT NULL,
    ""created_by_name"" ""text"" NOT NULL,
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""drawn_at"" timestamp with time zone,
    ""canceled_at"" timestamp with time zone,
    CONSTRAINT ""lotteries_pkey"" PRIMARY KEY (""id""),
    CONSTRAINT ""ux_lotteries_message_id"" UNIQUE (""message_id"")
);

CREATE INDEX IF NOT EXISTS ""idx_lotteries_guild_status"" ON ""public"".""lotteries"" USING ""btree"" (""guild_id"", ""status"", ""updated_at"" DESC);
CREATE INDEX IF NOT EXISTS ""idx_lotteries_close_at"" ON ""public"".""lotteries"" USING ""btree"" (""status"", ""close_at"" ASC);

--
-- Name: lottery_entries; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""lottery_entries"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""lottery_id"" integer NOT NULL,
    ""user_id"" bigint NOT NULL,
    ""user_name"" ""text"" NOT NULL,
    ""joined_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""lottery_entries_pkey"" PRIMARY KEY (""id""),
    CONSTRAINT ""ux_lottery_entries"" UNIQUE (""lottery_id"", ""user_id"")
);

ALTER TABLE ONLY ""public"".""lottery_entries""
    ADD CONSTRAINT ""lottery_entries_lottery_id_fkey"" FOREIGN KEY (""lottery_id"") REFERENCES ""public"".""lotteries""(""id"") ON DELETE CASCADE;

CREATE INDEX IF NOT EXISTS ""idx_lottery_entries_lottery"" ON ""public"".""lottery_entries"" USING ""btree"" (""lottery_id"", ""joined_at"" ASC);

--
-- Name: lottery_winners; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""lottery_winners"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""lottery_id"" integer NOT NULL,
    ""user_id"" bigint NOT NULL,
    ""user_name"" ""text"" NOT NULL,
    ""winner_order"" integer NOT NULL,
    ""drawn_by_user_id"" bigint NOT NULL,
    ""drawn_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""lottery_winners_pkey"" PRIMARY KEY (""id""),
    CONSTRAINT ""ux_lottery_winners"" UNIQUE (""lottery_id"", ""user_id"")
);

ALTER TABLE ONLY ""public"".""lottery_winners""
    ADD CONSTRAINT ""lottery_winners_lottery_id_fkey"" FOREIGN KEY (""lottery_id"") REFERENCES ""public"".""lotteries""(""id"") ON DELETE CASCADE;

CREATE INDEX IF NOT EXISTS ""idx_lottery_winners_lottery"" ON ""public"".""lottery_winners"" USING ""btree"" (""lottery_id"", ""winner_order"" ASC);

--
-- Name: bot_admin_operations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE IF NOT EXISTS ""public"".""bot_admin_operations"" (
    ""id"" integer NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""operation_type"" ""text"" NOT NULL,
    ""status"" ""text"" NOT NULL DEFAULT 'queued'::""text"",
    ""guild_id"" ""text"",
    ""requested_by_discord_user_id"" ""text"",
    ""requested_by_display_name"" ""text"",
    ""note"" ""text"",
    ""created_at_utc"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""updated_at_utc"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    ""completed_at_utc"" timestamp with time zone,
    CONSTRAINT ""bot_admin_operations_pkey"" PRIMARY KEY (""id"")
);

CREATE INDEX IF NOT EXISTS ""idx_bot_admin_operations_created"" ON ""public"".""bot_admin_operations"" USING ""btree"" (""created_at_utc"" DESC);
CREATE INDEX IF NOT EXISTS ""idx_bot_admin_operations_status"" ON ""public"".""bot_admin_operations"" USING ""btree"" (""status"", ""created_at_utc"" DESC);


CREATE TABLE IF NOT EXISTS ""public"".""sleep_kick_logs"" (
    ""id"" bigint NOT NULL GENERATED ALWAYS AS IDENTITY,
    ""guild_id"" bigint NOT NULL,
    ""target_user_id"" bigint NOT NULL,
    ""target_user_name"" ""text"",
    ""initiator_user_id"" bigint,
    ""initiator_user_name"" ""text"",
    ""voice_channel_id"" bigint NOT NULL,
    ""voice_channel_name"" ""text"",
    ""event_type"" ""text"" NOT NULL,
    ""vote_yes_count"" integer NOT NULL DEFAULT 0,
    ""vote_no_count"" integer NOT NULL DEFAULT 0,
    ""required_votes"" integer NOT NULL DEFAULT 0,
    ""voter_details"" jsonb,
    ""result"" ""text"",
    ""created_at"" timestamp with time zone DEFAULT ""now""() NOT NULL,
    CONSTRAINT ""sleep_kick_logs_pkey"" PRIMARY KEY (""id"")
);

CREATE INDEX IF NOT EXISTS ""idx_sleep_kick_logs_guild_created"" ON ""public"".""sleep_kick_logs"" USING ""btree"" (""guild_id"", ""created_at"" DESC);
CREATE INDEX IF NOT EXISTS ""idx_sleep_kick_logs_target"" ON ""public"".""sleep_kick_logs"" USING ""btree"" (""target_user_id"", ""created_at"" DESC);
CREATE INDEX IF NOT EXISTS ""idx_sleep_kick_logs_initiator"" ON ""public"".""sleep_kick_logs"" USING ""btree"" (""initiator_user_id"", ""created_at"" DESC);


--
-- PostgreSQL database dump complete
--

";
}
